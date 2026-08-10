using FlowX.Runtime;

namespace FlowX.Hosting;

/// <summary>
/// Starts one flow from one delivery, decoding the message into the flow's own input.
/// </summary>
/// <param name="host">The host that opens the journal and runs the plan.</param>
/// <param name="registration">The subscription and the flow it starts.</param>
/// <param name="invocation">The identity and tenancy this delivery is admitted under.</param>
/// <param name="message">What the broker delivered.</param>
/// <param name="instanceId">The id derived from the delivery, so a redelivery folds onto it.</param>
/// <param name="ct">The scan's cancellation.</param>
/// <remarks>
/// <strong>Generated, because generated code is the only place both types are known.</strong>
/// <see cref="FlowHost.RunAsync{TIn}(ExecutionPlan, IStepDispatcher, FlowInvocation, TIn, Guid, CancellationToken)"/>
/// seeds the context under <c>TIn</c>'s <em>static</em> type, so a starter that handed the
/// decoded value over as <c>object</c> would file the flow's input under a key no step looks
/// up. The scan is not generic over a flow's input and cannot be; the emitted lambda closes
/// over the decoder and the contract together.
/// </remarks>
public delegate ValueTask<FlowExecutionResult> BusStarter(
    FlowHost host,
    BusRegistration registration,
    FlowInvocation invocation,
    BusMessage message,
    Guid instanceId,
    CancellationToken ct);

/// <summary>One subscription this node serves, and the flow it starts.</summary>
/// <param name="Subscription">What it consumes, and under whose group.</param>
/// <param name="Flow">The compiled plan and its dispatcher.</param>
/// <param name="Starter">
/// How a delivery becomes this flow's input, or <c>null</c> when the flow takes the delivery
/// itself and the scan can start it directly.
/// </param>
public sealed record BusRegistration(
    BusSubscription Subscription, FlowRegistration Flow, BusStarter? Starter = null)
{
    /// <summary>The id the instance for one delivery of this subscription is started under.</summary>
    /// <param name="eventId">The message's own identity.</param>
    public Guid InstanceIdFor(Guid eventId) => BusDeliveryIdentity.InstanceIdFor(
        Subscription.FlowId, Subscription.FlowVersion, Subscription.Topic, Subscription.Group, eventId);

    /// <summary>The lease that makes this node the only one reading one partition.</summary>
    /// <param name="partitionKey">The key, or null for the partition of unordered events.</param>
    public Guid PartitionLeaseFor(string? partitionKey) => BusDeliveryIdentity.StreamLeaseIdFor(
        Subscription.FlowId,
        Subscription.FlowVersion,
        Subscription.Topic,
        Subscription.Group,
        partitionKey);
}

/// <summary>
/// Which subscriptions this node serves, and the plans behind them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The shape <see cref="FlowScheduleCatalog"/> has, and separate from
/// <see cref="FlowCatalog"/> for its reason.</strong> <see cref="FlowCatalog"/> maps an identity a
/// journal row already carries back to a plan — a recovery sweep finds an instance and has to work
/// out what it is. This one holds declarations that have <em>no</em> instance yet and is walked in
/// the other direction: for each subscription, has the broker anything.
/// </para>
/// <para>
/// Registered rather than discovered, for <see cref="FlowCatalog"/>'s reason: reflecting over
/// loaded assemblies to find <c>[BusTrigger]</c> would be a trim-time dependency on types nothing
/// statically references, which constraint C2 forbids — and <c>samples/ecommerce</c> is the
/// repository's only NativeAOT-published assembly, so the constraint is not hypothetical. The
/// generated <c>AddFlowXSubscriptions</c> is a call per subscription and is legible in a stack
/// trace.
/// </para>
/// </remarks>
public sealed class FlowBusCatalog
{
    private readonly Lock _gate = new();
    private readonly Dictionary<SubscriptionKey, BusRegistration> _subscriptions = [];

    /// <summary>How many subscriptions this node serves.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _subscriptions.Count;
            }
        }
    }

    /// <summary>Every registered subscription, ordered so a pass is deterministic.</summary>
    public IReadOnlyList<BusRegistration> Registrations
    {
        get
        {
            lock (_gate)
            {
                return [.. _subscriptions
                    .OrderBy(static entry => entry.Key.FlowId, StringComparer.Ordinal)
                    .ThenBy(static entry => entry.Key.FlowVersion, StringComparer.Ordinal)
                    .ThenBy(static entry => entry.Key.Topic, StringComparer.Ordinal)
                    .ThenBy(static entry => entry.Key.Group, StringComparer.Ordinal)
                    .Select(static entry => entry.Value)];
            }
        }
    }

    /// <summary>Makes a subscription consumable on this node.</summary>
    /// <param name="subscription">What it consumes, and under whose group.</param>
    /// <param name="plan">The compiled flow.</param>
    /// <param name="dispatcher">Invokes the capability behind each step index.</param>
    /// <returns>The same catalogue, so registrations chain.</returns>
    /// <exception cref="ArgumentException">
    /// The plan is not the flow the subscription names, or it does not declare
    /// <see cref="ExecutionProfile.Durable"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <strong>An ephemeral flow is refused, and this is the load-bearing check in the
    /// type.</strong> Nothing journals an ephemeral instance, so the id a delivery derives
    /// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0035-a-delivery-names-the-instance-it-starts.md">ADR-0035</a>)
    /// is inert: <c>FlowHost</c> takes no lease, writes no row, and every redelivery of one
    /// message runs the flow again. That is the "at-least-once means at-least-once execution"
    /// failure with no symptom at all — no error, no duplicate-key refusal, nothing to count.
    /// Refusing here turns it into a pod that never becomes ready. <c>FLOWX1039</c> is the
    /// earlier half of the same rule and catches it at compile time; this one catches a
    /// hand-written registration and a plan that changed profile after the code that registers it
    /// was generated.
    /// </para>
    /// <para>
    /// Last registration wins for a given <c>(id, version, topic, group)</c>, for
    /// <see cref="FlowCatalog.Add"/>'s reason: a duplicate is a composition root registering
    /// twice, and throwing would make an idempotent one a startup crash. Two different topics, or
    /// one topic under two groups, are two subscriptions and both are kept.
    /// </para>
    /// </remarks>
    public FlowBusCatalog Add(BusSubscription subscription, ExecutionPlan plan, IStepDispatcher dispatcher)
        => Add(subscription, plan, dispatcher, starter: null);

    /// <summary>Registers a subscription whose deliveries are decoded into the flow's input.</summary>
    /// <param name="subscription">What it consumes, and under whose group.</param>
    /// <param name="plan">The compiled flow.</param>
    /// <param name="dispatcher">The flow's generated dispatcher.</param>
    /// <param name="starter">
    /// The generated decode-and-run, or <c>null</c> for a flow that takes the delivery itself.
    /// </param>
    /// <returns>This catalog, so registrations chain.</returns>
    public FlowBusCatalog Add(
        BusSubscription subscription, ExecutionPlan plan, IStepDispatcher dispatcher, BusStarter? starter)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dispatcher);

        if (!string.Equals(plan.Flow.Id, subscription.FlowId, StringComparison.Ordinal) ||
            !string.Equals(plan.Flow.Version, subscription.FlowVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The subscription names '{subscription.FlowId}@{subscription.FlowVersion}' and " +
                $"the plan is '{plan.Flow.Id}@{plan.Flow.Version}'. The identity is what the " +
                "instance id is derived from, so a mismatch would start one flow under another's " +
                "deliveries.",
                nameof(plan));
        }

        if (plan.Flow.Profile != ExecutionProfile.Durable)
        {
            throw new ArgumentException(
                $"Flow '{subscription.FlowId}' subscribes to '{subscription.Topic}' and declares " +
                $"the profile '{plan.Flow.Profile}'. A bus-triggered flow must declare Durable: " +
                "nothing journals an ephemeral instance, so the id a delivery derives is inert, " +
                "there is no primary key to refuse a redelivery, and one message would start one " +
                "flow per delivery — with no error, no duplicate row and nothing anywhere to count.",
                nameof(plan));
        }

        lock (_gate)
        {
            _subscriptions[new SubscriptionKey(
                subscription.FlowId, subscription.FlowVersion, subscription.Topic, subscription.Group)] =
                new BusRegistration(subscription, new FlowRegistration(plan, dispatcher), starter);
        }

        return this;
    }

    /// <summary>Ordinal by construction: an id, a version, a topic and a group are identifiers.</summary>
    private readonly record struct SubscriptionKey(
        string FlowId, string FlowVersion, string Topic, string Group);
}
