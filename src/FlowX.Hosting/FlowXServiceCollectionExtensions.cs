using FlowX.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace FlowX.Hosting;

/// <summary>Registers the FlowX runtime with a dependency-injection container.</summary>
public static class FlowXServiceCollectionExtensions
{
    /// <summary>Adds the FlowX runtime, validating its configuration at startup.</summary>
    /// <param name="services">The container.</param>
    /// <param name="configure">Optional configuration callback.</param>
    /// <remarks>
    /// <para>
    /// Idempotent. A library registered twice — once by the application, once by a
    /// plugin that also depends on it — must not produce two engines with two
    /// independent context pools, which would silently double the process's memory
    /// floor and make pooling metrics meaningless.
    /// </para>
    /// <para>
    /// <c>ValidateOnStart</c> is the load-bearing call. Without it the same validation
    /// runs lazily, on the first request after a deploy, when traffic is already
    /// routed to the new pod (OWASP A05).
    /// </para>
    /// <para>
    /// <strong>Durability is opted into by registering stores, not by a flag here.</strong>
    /// An application that registers an <see cref="IFlowJournal"/> and an
    /// <see cref="ILeaseStore"/> gets a host that runs <c>Durable</c> flows; one that also
    /// registers an <see cref="IRecoveryIndex"/> gets a node that picks up instances a dead
    /// node left behind. One that registers neither keeps the behaviour WP-52 landed — a
    /// <c>Durable</c> flow refused with <c>flow.durability_not_configured</c> — which is
    /// what an unconfigured host should say. A store implementing more than one of the three
    /// must be registered under each interface it implements: the container matches on the
    /// service type, not on what the instance turns out to be.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddFlowX(
        this IServiceCollection services,
        Action<FlowXOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = services.AddOptions<FlowXOptions>();

        if (configure is not null)
        {
            builder.Configure(configure);
        }

        // TryAdd throughout: see the idempotency note above.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<FlowXOptions>, FlowXOptionsValidator>());

        builder.ValidateOnStart();

        services.TryAddSingleton<IClock>(SystemClock.Instance);

        // The seam ICompensationAlertSink was left for. Registered by default rather than
        // opted into: docs/12-Observability.md §7 pages immediately on any occurrence of
        // flowx_flow_compensation_failed_total, and a counter an application has to remember to
        // wire is a counter that reads zero on the deployment that needed it. An application
        // that registers its own sink — a pager, a dead-letter writer — wins, because TryAdd
        // does not replace it.
        services.TryAddSingleton<ICompensationAlertSink, CompensationFailureCounter>();

        services.TryAddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<FlowXOptions>>().Value;

            // The four policy seams are resolved rather than required, and there is deliberately
            // no default for any of them — TryAdd's bargain above does not transfer. A default
            // alert sink that counts leaves a deployment degraded about a state the instance row
            // still records; a default in-memory rate limiter would leave one admitting n × the
            // declared rate across n nodes behind a declaration that reads as a deployment-wide
            // bound, a default in-memory idempotency store would deduplicate one caller in n, and
            // an in-memory audit sink would be a compliance control that survives no restart.
            //
            // The absences do not mean the same thing. A step declaring a RateLimit, an
            // Idempotency window or an Audit with no store registered is refused rather than run
            // (ADR-0040 §2.2, ADR-0025 §2.4, ADR-0043 §2), which is loud and one registration
            // fixes. A step declaring a Cache with no IResultCache simply dispatches, which is
            // what it did before stage 5 existed (ADR-0025 §2.3): the cache is the one seam
            // whose absence costs latency rather than correctness.
            return new FlowEngine(
                provider.GetRequiredService<IClock>(),
                options.MaxPooledContexts,
                provider.GetService<ICompensationAlertSink>(),
                provider.GetService<IRateLimiterStore>(),
                provider.GetService<IIdempotencyStore>(),
                provider.GetService<IResultCache>(),
                provider.GetService<IAuditSink>(),

                // A breaker per tenant only where the deployment already bounds tenants against
                // each other. A shared breaker protects a shared downstream faster; this is the
                // deployment that said its downstreams are not shared.
                options.TenantIsolation != TenantIsolation.None && options.Fairness.IsEnabled);
        });

        // The catalogue is registered whether or not anything is put in it. It is only read
        // by the recovery scan, and a host with no flows registered simply finds no candidate
        // it can run — which is the same answer as an empty backlog and needs no branch.
        services.TryAddSingleton<FlowCatalog>();

        services.TryAddSingleton(provider => new FlowHost(
            provider.GetRequiredService<FlowEngine>(),
            provider.GetRequiredService<IOptions<FlowXOptions>>().Value,
            ResolveDurability(provider),
            tenants: null,

            // The same store a step's RateLimit spends against, under a different key. A second
            // limiter would be a second budget for the same server to keep, and ADR-0040's
            // argument against a process-local limiter applies with more force to a per-tenant
            // bound than to a per-capability one: the multiplier is the replica count and the
            // promise it breaks is contractual.
            provider.GetService<IRateLimiterStore>(),
            provider.GetRequiredService<IClock>()));

        // First in the list, so a node that declared an address nothing serves stops before the
        // sweeps that would otherwise run happily around the hole. What it converts is the one
        // failure mode this repository keeps finding: a declaration nothing executes, which is
        // silent by construction.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, FlowXStartupValidation>(
            static provider => new FlowXStartupValidation(
                provider.GetService<FlowXDeclaredTriggers>(),
                provider.GetRequiredService<FlowBusCatalog>(),
                provider.GetRequiredService<FlowChangeCatalog>(),
                provider.GetRequiredService<FlowScheduleCatalog>(),
                provider.GetRequiredService<FlowStreamCatalog>())));

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, FlowRecoveryService>(
            static provider => new FlowRecoveryService(
                ResolveScan(provider),
                provider.GetRequiredService<IOptions<FlowXOptions>>().Value)));

        // A second loop rather than a second query on the first, because they are two sweeps
        // over disjoint sets of rows on two intervals a deployment may reasonably set apart —
        // and because a host that can wake parked instances but cannot take over abandoned
        // ones, or the reverse, is a configuration each store decides for itself.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, FlowTimerService>(
            static provider => new FlowTimerService(
                ResolveTimerScan(provider),
                provider.GetRequiredService<IOptions<FlowXOptions>>().Value)));

        // Registered whether or not anything is put in it, for FlowCatalog's reason: the sweep
        // over an empty catalogue is not enabled, which is the same answer as a node that fires
        // nothing and needs no branch.
        services.TryAddSingleton<FlowScheduleCatalog>();

        // TryAdd, so a store that knows the tenant set better wins whichever order the two
        // registrations run in: AddFlowXPostgres registers its own over tenant_schema with a
        // plain AddSingleton, which is resolved in preference to this one when it comes second
        // and suppresses this one when it comes first.
        services.TryAddSingleton<ITenantDirectory>(static provider => new DeclaredTenantDirectory(
            [.. provider.GetRequiredService<IOptions<FlowXOptions>>().Value.Tenants]));

        // A third loop, and not a query on either of the first two. A schedule occurrence is
        // computed rather than read off a row, so this sweep touches no index at all — folding
        // it into FlowTimerService would mean one loop whose interval means two different
        // things, and a deployment that wants schedules at one-second resolution and durable
        // timers at ten cannot say so.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, FlowScheduleService>(
            static provider => new FlowScheduleService(
                ResolveScheduleScan(provider),
                provider.GetRequiredService<IOptions<FlowXOptions>>().Value)));

        // Registered whether or not anything is put in it, for FlowScheduleCatalog's reason.
        services.TryAddSingleton<FlowBusCatalog>();

        // A fourth loop, and not a query on any of the first three. A bus pass asks a broker
        // rather than an index of ours, so it has no interval in common with the sweeps that read
        // rows: a deployment reasonably wants sub-second consumption and ten-second recovery, and
        // one loop whose interval meant both could not give it either.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, FlowBusService>(
            static provider => new FlowBusService(
                ResolveBusScan(provider),
                provider.GetRequiredService<IOptions<FlowXOptions>>().Value)));

        // Registered whether or not anything is put in it, for FlowScheduleCatalog's reason.
        services.TryAddSingleton<FlowChangeCatalog>();

        // A fifth loop, and not a query on any of the first four. A change pass reads a cursor
        // and a table rather than a broker or an index of ours, and its latency floor is the
        // store's oldest open transaction rather than a poll interval — so a deployment that
        // wants sub-second consumption from a broker and a slower sweep over the outbox can say
        // so (ADR-0048).
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, FlowChangeService>(
            static provider => new FlowChangeService(
                ResolveChangeScan(provider),
                provider.GetRequiredService<IOptions<FlowXOptions>>().Value)));

        // Registered whether or not anything is put in it, for FlowScheduleCatalog's reason.
        services.TryAddSingleton<FlowStreamCatalog>();

        // A sixth loop. A stream pass holds a bounded channel and a set of open windows for the
        // life of the process, so it cannot share an interval with a sweep that is stateless
        // between passes — and its interval is a read cadence rather than a latency, because what
        // closes a window is a record's event time and not a clock (ADR-0056).
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, FlowStreamService>(
            static provider => new FlowStreamService(
                ResolveStreamScan(provider),
                provider.GetRequiredService<IOptions<FlowXOptions>>().Value)));

        services.TryAddSingleton<FlowXHealthCheck>();

        // Registering the type is not the same as registering the check. Before this,
        // FlowXHealthCheck was resolvable and never ran: an application that called
        // MapHealthChecks got an exception for the missing service, and one that also
        // called AddHealthChecks got a probe that reported healthy while draining. The
        // sample found it on its first startup, which is what samples are for.
        services.AddHealthChecks();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureOptions<HealthCheckServiceOptions>, FlowXHealthCheckRegistration>());

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, FlowXLifecycleService>());

        return services;
    }

    /// <summary>
    /// The stores this host was given, or null when it was given none.
    /// </summary>
    /// <remarks>
    /// Resolved through <see cref="ServiceProviderServiceExtensions.GetService{T}"/> rather
    /// than required, because an unconfigured host is a supported configuration and not a
    /// mistake. An application that wants to build the bundle itself — a store that plays two
    /// roles, or one wrapped in decorators — registers a <see cref="FlowDurability"/> and
    /// that wins.
    /// </remarks>
    private static FlowDurability? ResolveDurability(IServiceProvider provider)
    {
        if (provider.GetService<FlowDurability>() is { } configured)
        {
            return configured;
        }

        var journal = provider.GetService<IFlowJournal>();
        var leases = provider.GetService<ILeaseStore>();

        // Both or neither. A journal with no lease store would write under a token nothing
        // issued; a lease store with no journal would fence nothing.
        //
        // The journal is wrapped here and nowhere else, which is what makes
        // flowx_journal_commit_seconds a property of the contract rather than of one adapter.
        // The wrap is a no-op unless something is listening, and an application that supplies
        // its own FlowDurability above is left exactly as it built it.
        return journal is not null && leases is not null
            ? new FlowDurability(
                JournalTelemetry.Wrap(journal),
                leases,
                provider.GetService<IRecoveryIndex>(),
                provider.GetService<ITimerIndex>())
            : null;
    }

    /// <summary>The recovery sweep, or null when this host has nothing to sweep with.</summary>
    private static FlowRecoveryScan? ResolveScan(IServiceProvider provider)
    {
        if (ResolveDurability(provider) is not { CanScan: true } durability)
        {
            return null;
        }

        return new FlowRecoveryScan(
            provider.GetRequiredService<FlowHost>(),
            provider.GetRequiredService<FlowCatalog>(),
            durability,
            provider.GetRequiredService<IOptions<FlowXOptions>>().Value,
            provider.GetRequiredService<IClock>());
    }

    /// <summary>The timer sweep, or null when this host has nothing to sweep with.</summary>
    /// <remarks>
    /// Null is the state a deployment is in when its journal implements no
    /// <see cref="ITimerIndex"/>, and it is a supported one: durable flows still run and still
    /// park, and a <c>.Delay(...)</c> waits for whatever else resumes the instance. It is
    /// reported by <c>FlowTimerScan.IsEnabled</c> rather than refused, because a host that
    /// runs no flow with a timer in it is not misconfigured.
    /// </remarks>
    private static FlowTimerScan? ResolveTimerScan(IServiceProvider provider)
    {
        if (ResolveDurability(provider) is not { CanWake: true } durability)
        {
            return null;
        }

        return new FlowTimerScan(
            provider.GetRequiredService<FlowHost>(),
            provider.GetRequiredService<FlowCatalog>(),
            durability,
            provider.GetRequiredService<IOptions<FlowXOptions>>().Value,
            provider.GetRequiredService<IClock>());
    }

    /// <summary>The schedule sweep, or null when this host has no journal to fire into.</summary>
    /// <remarks>
    /// <para>
    /// Null when nothing registered a journal, which for a schedule is not a supported
    /// configuration in the way it is for a timer: without a primary key to refuse a second
    /// node's firing, a schedule fires once per node per occurrence and nothing records that it
    /// did. A host in that state fires nothing, and <c>FlowScheduleCatalog.Add</c> is where an
    /// application that meant to schedule something finds out.
    /// </para>
    /// <para>
    /// <strong>Whether anything is registered is deliberately not decided here.</strong>
    /// Schedules reach the catalogue from the composition root <em>after</em> the container is
    /// built, by the generated <c>AddFlowXSchedules</c>, exactly as a flow reaches
    /// <see cref="FlowCatalog"/>. So the sweep is constructed over a catalogue that may still be
    /// empty and reads <see cref="FlowScheduleScan.IsEnabled"/> at each tick.
    /// </para>
    /// </remarks>
    private static FlowScheduleScan? ResolveScheduleScan(IServiceProvider provider)
    {
        if (ResolveDurability(provider) is not { } durability)
        {
            return null;
        }

        return new FlowScheduleScan(
            provider.GetRequiredService<FlowHost>(),
            provider.GetRequiredService<FlowScheduleCatalog>(),
            durability,
            provider.GetRequiredService<IOptions<FlowXOptions>>().Value,
            provider.GetRequiredService<IClock>(),

            // Optional, because a per-tenant schedule is the only thing that reads it and most
            // deployments declare none. A host that wired one and no directory fires nothing
            // rather than firing once with no tenant.
            provider.GetService<ITenantDirectory>());
    }

    /// <summary>The bus pass, or null when this host has no broker or no journal.</summary>
    /// <remarks>
    /// <para>
    /// Two ways to be null, and they are different configurations. <strong>No
    /// <see cref="IBusConsumer"/></strong> is the ordinary state of an application that consumes
    /// nothing, and of one that declares <c>[BusTrigger]</c> and forgot to wire a broker — the
    /// second is a real mistake and is caught by <c>FlowBusScan.IsEnabled</c> being false rather
    /// than by a start-up failure, because a flow library referenced by a host that serves only
    /// some of its subscriptions is legitimate. <strong>No journal</strong> is not a supported
    /// configuration for a subscription at all, for <see cref="ResolveScheduleScan"/>'s reason
    /// one transport over: without a primary key to refuse a redelivery, one message starts one
    /// flow per delivery and nothing records that it did. <c>FlowBusCatalog.Add</c> is where an
    /// application that meant to subscribe finds out.
    /// </para>
    /// <para>
    /// <strong>Whether anything is registered is deliberately not decided here.</strong>
    /// Subscriptions reach the catalogue from the composition root <em>after</em> the container is
    /// built, by the generated <c>AddFlowXSubscriptions</c>, exactly as a flow reaches
    /// <see cref="FlowCatalog"/>.
    /// </para>
    /// </remarks>
    private static FlowBusScan? ResolveBusScan(IServiceProvider provider)
    {
        if (provider.GetService<IBusConsumer>() is not { } consumer ||
            ResolveDurability(provider) is not { } durability)
        {
            return null;
        }

        return new FlowBusScan(
            provider.GetRequiredService<FlowHost>(),
            provider.GetRequiredService<FlowBusCatalog>(),
            consumer,
            durability,
            provider.GetRequiredService<IOptions<FlowXOptions>>().Value);
    }

    /// <summary>The change pass, or null when this host has no feed or no journal.</summary>
    /// <remarks>
    /// <see cref="ResolveBusScan"/>'s two ways to be null, unchanged. No <see cref="IChangeFeed"/>
    /// is the ordinary state of an application that observes nothing, and no journal is not a
    /// supported configuration for a change subscription at all — <c>FlowChangeCatalog.Add</c> is
    /// where an application that meant to observe finds out.
    /// </remarks>
    private static FlowChangeScan? ResolveChangeScan(IServiceProvider provider)
    {
        if (provider.GetService<IChangeFeed>() is not { } feed ||
            ResolveDurability(provider) is not { } durability)
        {
            return null;
        }

        return new FlowChangeScan(
            provider.GetRequiredService<FlowHost>(),
            provider.GetRequiredService<FlowChangeCatalog>(),
            feed,
            durability,
            provider.GetRequiredService<IOptions<FlowXOptions>>().Value);
    }

    /// <summary>
    /// The stream pass, or null when this host is missing one of the four things it needs.
    /// </summary>
    /// <remarks>
    /// <strong>The side output is required, and that is the point.</strong> docs/09 §9 promises
    /// that a record later than the declared lateness is "routed to a side output rather than
    /// dropped silently"; a host with no <see cref="IStreamSideOutput"/> has nowhere to route
    /// one, and defaulting to a sink that discards would be the silent drop with a type name on
    /// it. So the pass does not exist, and <c>FlowStreamCatalog.Add</c> is where an application
    /// that meant to read a stream finds out it registered no sink.
    /// </remarks>
    private static FlowStreamScan? ResolveStreamScan(IServiceProvider provider)
    {
        if (provider.GetService<IStreamSource>() is not { } source ||
            provider.GetService<IStreamCheckpointStore>() is not { } checkpoints ||
            provider.GetService<IStreamSideOutput>() is not { } sideOutput ||
            ResolveDurability(provider) is not { } durability)
        {
            return null;
        }

        return new FlowStreamScan(
            provider.GetRequiredService<FlowHost>(),
            provider.GetRequiredService<FlowStreamCatalog>(),
            source,
            checkpoints,
            sideOutput,
            durability,
            provider.GetRequiredService<IOptions<FlowXOptions>>().Value);
    }
}

/// <summary>
/// What generated stream-subscription registration code calls, and the only thing it knows about
/// this assembly.
/// </summary>
/// <remarks>
/// <see cref="FlowChangeSubscriptionRegistration"/>'s shape and reasons. It takes the window
/// declaration as the strings the attribute carried, because the generated call site is C# the
/// compiler writes from attribute data, and attribute data is strings.
/// </remarks>
public static class FlowStreamSubscriptionRegistration
{
    /// <summary>Registers one declared stream subscription on this node.</summary>
    /// <param name="services">The built container, which is where the dispatcher comes from.</param>
    /// <param name="plan">The compiled flow.</param>
    /// <param name="dispatcher">Resolves the flow's generated dispatcher.</param>
    /// <param name="source">The stream, exactly as the manifest published it.</param>
    /// <param name="window">The declared window, e.g. <c>tumbling:1m</c>.</param>
    /// <param name="lateness">The declared lateness, an ISO-8601 duration.</param>
    /// <param name="checkpoint">The declared checkpoint interval, an ISO-8601 duration.</param>
    /// <param name="parallelism">How many closed windows may run at once.</param>
    /// <returns>The same provider, so registrations chain.</returns>
    /// <exception cref="ArgumentException">
    /// The flow does not declare <c>Streaming</c>, or the window is a shape the engine does not
    /// implement. Both are startup failures on purpose — see <see cref="FlowStreamCatalog.Add"/>.
    /// </exception>
    public static IServiceProvider Add(
        IServiceProvider services,
        ExecutionPlan plan,
        Func<IServiceProvider, IStepDispatcher> dispatcher,
        string source,
        string window,
        string lateness,
        string checkpoint,
        int parallelism)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dispatcher);

        services.GetRequiredService<FlowStreamCatalog>().Add(
            // The group is empty because [StreamTrigger] declares none, and it needs none: a
            // subscription is already keyed on the flow's id and version, so two flows reading one
            // stream keep two checkpoints and one flow cannot declare the same source twice. The
            // term is kept in StreamSubscription so that the identity derivation has the same four
            // terms as every other trigger's, rather than a special case one node could get wrong.
            new StreamSubscription(plan.Flow.Id, plan.Flow.Version, source, string.Empty),
            window,
            lateness,
            checkpoint,
            parallelism,
            plan,
            dispatcher(services));

        // A windowed instance is a journaled instance like any other: a node that dies holding
        // one has abandoned it, and a recovery sweep can only take it over if this node can turn
        // its (flow_id, flow_version) back into a plan.
        services.GetRequiredService<FlowCatalog>().Add(plan, dispatcher(services));

        return services;
    }
}

/// <summary>
/// What generated change-subscription registration code calls, and the only thing it knows about
/// this assembly.
/// </summary>
/// <remarks>
/// <see cref="FlowBusSubscriptionRegistration"/>'s shape and reasons. The generator knows this
/// assembly only by the string <c>"FlowX.Hosting.FlowChangeSubscriptionRegistration"</c>, which it
/// looks up in the user's own compilation before emitting anything.
/// </remarks>
public static class FlowChangeSubscriptionRegistration
{
    /// <summary>Registers one declared change subscription on this node.</summary>
    /// <param name="services">The built container, which is where the dispatcher comes from.</param>
    /// <param name="plan">The compiled flow.</param>
    /// <param name="dispatcher">Resolves the flow's generated dispatcher.</param>
    /// <param name="source">The observed event type, exactly as the manifest published it.</param>
    /// <param name="group">The subscription group, exactly as the manifest published it.</param>
    /// <returns>The same provider, so registrations chain.</returns>
    /// <exception cref="ArgumentException">
    /// The flow does not declare <c>Durable</c>, or it emits the type it observes. Both are
    /// startup failures on purpose — see <see cref="FlowChangeCatalog.Add"/>.
    /// </exception>
    /// <remarks>
    /// <strong>There is no transport to check</strong>, unlike
    /// <see cref="FlowBusSubscriptionRegistration.Add"/>: a change trigger names no feed family,
    /// so there is nothing on the declaration for a wired <see cref="IChangeFeed"/> to disagree
    /// with.
    /// </remarks>
    public static IServiceProvider Add(
        IServiceProvider services,
        ExecutionPlan plan,
        Func<IServiceProvider, IStepDispatcher> dispatcher,
        string source,
        string group)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dispatcher);

        services.GetRequiredService<FlowChangeCatalog>().Add(
            new ChangeSubscription(plan.Flow.Id, plan.Flow.Version, source, group),
            plan,
            dispatcher(services));

        // An observed instance is a durable instance like any other: a node that dies holding one
        // has abandoned it, and a recovery sweep can only take it over if this node can turn its
        // (flow_id, flow_version) back into a plan.
        services.GetRequiredService<FlowCatalog>().Add(plan, dispatcher(services));

        return services;
    }
}

/// <summary>
/// What generated subscription registration code calls, and the only thing it knows about this
/// assembly.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A named type with a stable signature, for <c>FlowScheduleRegistration</c>'s
/// reason.</strong> The generator links against nothing and knows this assembly only by the string
/// <c>"FlowX.Hosting.FlowBusSubscriptionRegistration"</c>, which it looks up in the user's own
/// compilation before emitting anything. An application that does not reference
/// <c>FlowX.Hosting</c> gets no file, no type and no IL.
/// </para>
/// <para>
/// <strong>It takes primitives and not a <see cref="BusSubscription"/>.</strong> The generated
/// call site is C# the compiler writes from attribute data, and attribute data is strings.
/// </para>
/// </remarks>
public static class FlowBusSubscriptionRegistration
{
    /// <summary>Registers one declared subscription on this node.</summary>
    /// <param name="services">The built container, which is where the dispatcher comes from.</param>
    /// <param name="plan">The compiled flow.</param>
    /// <param name="dispatcher">Resolves the flow's generated dispatcher.</param>
    /// <param name="topic">The topic, exactly as the manifest published it.</param>
    /// <param name="group">The consumer group, exactly as the manifest published it.</param>
    /// <param name="transport">
    /// The broker family the declaration named, or null when it named none.
    /// </param>
    /// <param name="starter">
    /// Generated decode-and-run for a flow whose input is its own contract rather than the
    /// delivery, or null when the flow takes the delivery. See <see cref="BusStarter"/> for why
    /// this is a delegate the generator writes rather than a decoder this method resolves.
    /// </param>
    /// <returns>The same provider, so registrations chain.</returns>
    /// <exception cref="ArgumentException">
    /// The flow does not declare <c>Durable</c>, or a broker is wired that does not serve the
    /// declared transport. Both are startup failures on purpose — see
    /// <see cref="FlowBusCatalog.Add(BusSubscription, ExecutionPlan, IStepDispatcher)"/>.
    /// </exception>
    public static IServiceProvider Add(
        IServiceProvider services,
        ExecutionPlan plan,
        Func<IServiceProvider, IStepDispatcher> dispatcher,
        string topic,
        string group,
        string? transport = null,
        BusStarter? starter = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dispatcher);

        // A [KafkaTrigger] on a host wired for a different bus is refused here, loudly. Serving
        // it anyway would consume from the wrong broker while the manifest went on publishing
        // "transport": "kafka" to everyone reading it — a documented-but-false claim, which is
        // the class of defect the manifest exists to eliminate.
        if (transport is not null &&
            services.GetService<IBusConsumer>() is { } consumer &&
            !string.Equals(consumer.Transport, transport, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Flow '{plan.Flow.Id}' declares a '{transport}' subscription to '{topic}' and " +
                $"this host has wired an IBusConsumer serving '{consumer.Transport}'. Serving it " +
                "anyway would consume from a broker the manifest does not name. Declare " +
                "[BusTrigger] if the flow is genuinely transport-neutral, or wire the broker the " +
                "flow names.",
                nameof(transport));
        }

        services.GetRequiredService<FlowBusCatalog>().Add(
            new BusSubscription(plan.Flow.Id, plan.Flow.Version, topic, group, transport),
            plan,
            dispatcher(services),
            starter);

        // A consumed instance is a durable instance like any other: a node that dies holding one
        // has abandoned it, and a recovery sweep can only take it over if this node can turn its
        // (flow_id, flow_version) back into a plan.
        services.GetRequiredService<FlowCatalog>().Add(plan, dispatcher(services));

        return services;
    }
}

/// <summary>
/// What generated schedule registration code calls, and the only thing it knows about this
/// assembly.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A named type with a stable signature, for <c>FlowEndpointExtensions</c>'s
/// reason.</strong> The generator links against nothing — it is a netstandard2.0 analyzer —
/// and knows this transport only as the string
/// <c>"FlowX.Hosting.FlowScheduleRegistration"</c>, which it looks up in the user's own
/// compilation before emitting anything. An application that does not reference
/// <c>FlowX.Hosting</c> gets no file, no type and no IL.
/// </para>
/// <para>
/// <strong>It takes primitives and not a <see cref="FlowSchedule"/>.</strong> The generated
/// call site is C# the compiler writes from attribute data, and attribute data is strings and
/// enum members; asking it to construct a parsed schedule would mean emitting the parse, which
/// belongs here where it can fail loudly at startup.
/// </para>
/// </remarks>
public static class FlowScheduleRegistration
{
    /// <summary>Registers one declared schedule on this node.</summary>
    /// <param name="services">The built container, which is where the dispatcher comes from.</param>
    /// <param name="plan">The compiled flow.</param>
    /// <param name="dispatcher">Resolves the flow's generated dispatcher.</param>
    /// <param name="cron">The five-field expression, exactly as the manifest published it.</param>
    /// <param name="timeZone">The IANA zone the expression is read in.</param>
    /// <param name="missedFire">Behaviour after downtime.</param>
    /// <param name="perTenant">
    /// Whether one occurrence is one firing per tenant, from <c>CronTriggerAttribute.PerTenant</c>.
    /// </param>
    /// <param name="overlap">
    /// What happens when the previous firing is still running, from
    /// <c>CronTriggerAttribute.Overlap</c>.
    /// </param>
    /// <param name="jitter">
    /// The ISO-8601 window a firing is released within, from <c>CronTriggerAttribute.Jitter</c>,
    /// or null for a schedule that fires on its occurrence.
    /// </param>
    /// <returns>The same provider, so registrations chain.</returns>
    /// <exception cref="ArgumentException">
    /// The expression, the zone or the jitter could not be read, or the flow does not declare
    /// <c>Durable</c>. All are startup failures on purpose — see
    /// <see cref="FlowScheduleCatalog.Add"/>.
    /// </exception>
    public static IServiceProvider Add(
        IServiceProvider services,
        ExecutionPlan plan,
        Func<IServiceProvider, IStepDispatcher> dispatcher,
        string cron,
        string timeZone,
        MissedFirePolicy missedFire,
        bool perTenant = false,
        OverlapPolicy overlap = OverlapPolicy.Skip,
        string? jitter = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dispatcher);

        services.GetRequiredService<FlowScheduleCatalog>().Add(
            FlowSchedule.Create(
                plan.Flow.Id,
                plan.Flow.Version,
                cron,
                timeZone,
                missedFire,
                perTenant,
                overlap,
                jitter),
            plan,
            dispatcher(services));

        // A scheduled instance is a durable instance like any other: a node that dies holding
        // one has abandoned it, and a recovery sweep can only take it over if this node can
        // turn its (flow_id, flow_version) back into a plan. Registering here rather than
        // asking the application to remember means a fired schedule is recoverable by
        // construction.
        services.GetRequiredService<FlowCatalog>().Add(plan, dispatcher(services));

        return services;
    }
}

/// <summary>
/// Adds <see cref="FlowXHealthCheck"/> to the health-check set.
/// </summary>
/// <remarks>
/// Done through <see cref="IConfigureOptions{TOptions}"/> rather than
/// <c>AddHealthChecks().AddCheck&lt;T&gt;()</c> so that <c>TryAddEnumerable</c> can
/// deduplicate it by implementation type. Calling <c>AddCheck</c> twice registers the
/// probe twice, and <c>AddFlowX</c> promises to be idempotent.
/// </remarks>
internal sealed class FlowXHealthCheckRegistration : IConfigureOptions<HealthCheckServiceOptions>
{
    /// <summary>The probe's name, as it appears in the health report.</summary>
    internal const string Name = "flowx";

    /// <summary>
    /// Tagged <c>ready</c>, not <c>live</c>. A liveness probe wired to this would restart
    /// a pod that is draining correctly — see <see cref="FlowXHealthCheck"/>.
    /// </summary>
    internal const string ReadyTag = "ready";

    public void Configure(HealthCheckServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Registrations.Add(new HealthCheckRegistration(
            Name,
            provider => provider.GetRequiredService<FlowXHealthCheck>(),
            failureStatus: HealthStatus.Unhealthy,
            tags: [ReadyTag]));
    }
}

/// <summary>
/// Marks the host ready on start, and drains in-flight flows on stop.
/// </summary>
/// <remarks>
/// Registered as an <see cref="IHostedService"/> so the drain happens inside the
/// host's own shutdown sequence, before the process exits. Doing it from a
/// <c>ProcessExit</c> handler instead — the other obvious place — gives no
/// cancellation token, no ordering guarantee against other services, and a hard
/// two-second limit on some runtimes.
/// </remarks>
internal sealed class FlowXLifecycleService : IHostedService
{
    private readonly FlowHost _host;
    private readonly FlowXOptions _options;
    private readonly IRateLimiterStore? _limiter;

    public FlowXLifecycleService(FlowHost host, IOptions<FlowXOptions> options, IRateLimiterStore? limiter = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);

        _host = host;
        _options = options.Value;
        _limiter = limiter;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // A registration this options validator cannot see, checked at the first moment
        // anything can: a per-tenant rate limit, quota or journal write budget declared with no
        // store to spend it against would refuse every call at run time, and a pod that never
        // becomes ready is the cheaper failure. The bulkhead is exempt because it is per node
        // and needs no store.
        var fairness = _options.Fairness;

        if ((fairness.PermitsPerWindow > 0
                || fairness.QuotaPerWindow > 0
                || fairness.BoundsJournalWrites)
            && _limiter is null)
        {
            throw new InvalidOperationException(
                $"{nameof(FlowXOptions.Fairness)} declares a per-tenant rate limit, quota or " +
                "journal write budget and no IRateLimiterStore is registered, so no budget " +
                "could be consulted. A per-tenant budget each node kept for itself would be " +
                "the declared limit times the replica count (ADR-0040): register a shared " +
                "limiter, or remove the bound.");
        }

        _host.MarkReady();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // The return value is deliberately not thrown on. A drain that ran out of
        // budget is a fact to report, not a reason to fail shutdown — failing here
        // would leave the process in a worse state than the abandoned work does.
        await _host.DrainAsync(cancellationToken).ConfigureAwait(false);
    }
}
