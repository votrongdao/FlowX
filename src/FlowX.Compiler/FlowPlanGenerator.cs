using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using FlowX.Compiler.Analysis;
using FlowX.Compiler.Diagnostics;
using FlowX.Compiler.Emit;
using FlowX.Compiler.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace FlowX.Compiler;

/// <summary>
/// Emits a compiled execution plan and step dispatcher for every <c>[Flow]</c> in the
/// compilation.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately thin. Everything this class does is: find flow declarations, hand each
/// to <see cref="FlowAnalyzer"/>, hand the resulting model to <see cref="FlowEmitter"/>,
/// and report whatever diagnostics came back. All of the judgement lives in the two
/// layers it calls, and both are testable without a compilation.
/// </para>
/// <para>
/// <strong>Incremental, and it matters.</strong> A generator that re-runs on every
/// keystroke makes the IDE unusable on a large solution, which is how a team ends up
/// disabling the generator and hand-writing what it produced. The pipeline is keyed on
/// syntax that names <c>[Flow]</c> so typing inside an unrelated method costs nothing,
/// and budget B12 caps total build overhead at 8 %.
/// </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class FlowPlanGenerator : IIncrementalGenerator
{
    private const string FlowAttributeName = "FlowX.FlowAttribute";
    private const string CapabilityAttributeName = "FlowX.CapabilityAttribute";
    private const string JsonSerializableAttributeName = "System.Text.Json.Serialization.JsonSerializableAttribute";
    private const string JsonSerializerContextName = "System.Text.Json.Serialization.JsonSerializerContext";

    /// <summary>
    /// The one thing this generator knows about the HTTP transport: a name to look for.
    /// </summary>
    /// <remarks>
    /// Not a reference. A Roslyn component links against nothing but Roslyn, and a
    /// generator that referenced <c>FlowX.Http</c> would put a plugin underneath the
    /// compiler — the exact inversion <c>RuntimeDoesNotReferenceAnyPlugin</c> exists to
    /// forbid. Looking the type up in the user's compilation asks the only question that
    /// matters, "did <em>they</em> reference it", and answers it without a dependency.
    /// </remarks>
    private const string HttpEndpointExtensionsName = "FlowX.Http.FlowEndpointExtensions";

    /// <summary>
    /// The one thing this generator knows about the host that fires a schedule: a name to look
    /// for.
    /// </summary>
    /// <remarks>
    /// The same arrangement <see cref="HttpEndpointExtensionsName"/> has, and for the same
    /// reason. <c>FlowX.Hosting</c> is not a plugin, but the generator cannot reference it either
    /// — it is a netstandard2.0 analyzer — and a flow library compiled on its own has no reason
    /// to depend on a host. Asking the user's compilation whether the type exists answers the
    /// only question that matters.
    /// </remarks>
    private const string ScheduleRegistrationName = "FlowX.Hosting.FlowScheduleRegistration";

    /// <summary>
    /// The one thing this generator knows about the host that consumes a subscription: a name to
    /// look for.
    /// </summary>
    /// <remarks>
    /// The same arrangement <see cref="ScheduleRegistrationName"/> has, and for the same reason.
    /// Note what is deliberately <em>not</em> looked up: any broker. Which bus serves a
    /// subscription is an <c>IBusConsumer</c> the host registers at run time, so a flow library
    /// compiled against no broker at all still emits its registrations — which is quality goal
    /// Q4 holding at build time as well as at run time.
    /// </remarks>
    private const string BusRegistrationName = "FlowX.Hosting.FlowBusSubscriptionRegistration";

    /// <summary>
    /// The host type a change-subscription registration calls, looked up by name for
    /// <see cref="BusRegistrationName"/>'s reason: a flow library that references no host emits
    /// no registration and no IL.
    /// </summary>
    private const string ChangeRegistrationName = "FlowX.Hosting.FlowChangeSubscriptionRegistration";

    /// <summary>
    /// The host type a stream-subscription registration calls, looked up by name for
    /// <see cref="BusRegistrationName"/>'s reason.
    /// </summary>
    private const string StreamRegistrationName = "FlowX.Hosting.FlowStreamSubscriptionRegistration";

    /// <summary>
    /// The one thing this generator knows about the agent surface: a name to look for.
    /// </summary>
    /// <remarks>
    /// The same arrangement <see cref="HttpEndpointExtensionsName"/> has, and for the same
    /// reason — <c>FlowX.Mcp</c> is a plugin, and a generator that referenced it would put a
    /// plugin underneath the compiler. Note what this binding does <em>not</em> need to look up:
    /// anything that describes the tool. The description, the confirmation mode, the required
    /// permissions and the declared side effects are already in the manifest this same run
    /// writes, and <c>FlowX.Mcp</c> reads them back from there.
    /// </remarks>
    private const string AgentToolRegistrationName = "FlowX.Mcp.FlowAgentToolRegistration";

    /// <summary>
    /// What a generated capability registration needs to exist: the container's own extension
    /// point.
    /// </summary>
    /// <remarks>
    /// Looked up rather than assumed, for <see cref="HttpEndpointExtensionsName"/>'s reason. A
    /// flow library that references only <c>FlowX.Abstractions</c> has no
    /// <c>IServiceCollection</c>, and emitting an extension method over a type that is not there
    /// would turn a working library into a build error for a convenience it never asked for.
    /// </remarks>
    private const string ServiceCollectionExtensionsName =
        "Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions";

    /// <summary>What a host that can refuse to start over an unserved address is called.</summary>
    private const string TriggerDeclarationName = "FlowX.Hosting.FlowXTriggerDeclaration";

    /// <summary>The id whose reporting this class decides rather than passes through.</summary>
    private static readonly string EmitDiagnosticId = FlowXDiagnostics.EmitIsNotYetPublished.Id;

    /// <summary>The second such id, and it is settled the same way for the same reason.</summary>
    private static readonly string StateDiagnosticId = FlowXDiagnostics.StateIsNotSerialisable.Id;

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var flows = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                FlowAttributeName,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, cancellationToken) => Analyze(ctx, cancellationToken))
            .Where(static result => result is not null);

        // The manifest describes the whole application, so it needs every flow at once.
        // Collect() introduces a barrier — any flow changing regenerates the manifest —
        // which is correct: a manifest built from a stale subset would be worse than no
        // manifest, because `flowx diff` would trust it.
        var application = context.CompilationProvider.Select(
            static (compilation, _) => compilation.AssemblyName ?? "Application");

        // Source pointers in the manifest are written relative to this, so the document
        // does not carry the build agent's directory layout. Supplied by the props file
        // shipped in the analyzer package; null when a host does not provide it, which
        // Relativise handles by leaving the path alone.
        var projectDirectory = context.AnalyzerConfigOptionsProvider.Select(
            static (options, _) => options.GlobalOptions.TryGetValue("build_property.projectdir", out var dir)
                ? dir
                : null);

        // Triggers are read from attributes on the flow's class rather than from its
        // Define chain, so they come down their own pipeline and never enter FlowModel.
        // The plan emitter has no use for them — the flow body cannot observe a trigger
        // (ADR-0004) — and keeping them out of the model is what makes that true in the
        // code and not only in the documentation.
        var triggers = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                FlowAttributeName,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, cancellationToken) => ReadTriggers(ctx, cancellationToken))
            .Where(static result => result is not null);

        // Error catalogues are keyed on [Capability], not on [Flow], so every capability
        // in the compilation is read — including one no flow has a step for yet. The
        // manifest joins on id@version and uses what it needs.
        var errorCatalogues = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                CapabilityAttributeName,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, cancellationToken) => ErrorCatalogueReader.Read(
                    ctx.TargetSymbol as INamedTypeSymbol, ctx.SemanticModel.Compilation, cancellationToken))
            .Where(static result => result is not null);

        context.RegisterSourceOutput(
            flows.Collect()
                .Combine(triggers.Collect())
                .Combine(errorCatalogues.Collect())
                .Combine(application)
                .Combine(projectDirectory),
            static (production, data) =>
            {
                var ((((analysed, declared), catalogues), applicationName), directory) = data;
                ProduceManifest(production, analysed, declared, catalogues, applicationName, directory);
            });

        // Whether this compilation can host an HTTP endpoint at all, expressed as one
        // bool so nothing downstream re-runs when an unrelated reference changes. The
        // generator knows the transport only by this name: it links against no plugin
        // and could not, being a netstandard2.0 analyzer, which is what keeps
        // RuntimeDoesNotReferenceAnyPlugin true by construction rather than by care.
        var httpAvailable = context.CompilationProvider.Select(
            static (compilation, _) => compilation.GetTypeByMetadataName(HttpEndpointExtensionsName) is not null);

        // The serialiser contexts declared in this compilation. Read from
        // [JsonSerializable] rather than from the properties System.Text.Json generates
        // from it: the attribute is the author's declaration and is readable now,
        // whereas the properties are another generator's output and would put an
        // ordering dependency between two generators that have none.
        var jsonContexts = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                JsonSerializableAttributeName,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, cancellationToken) => ReadJsonContext(ctx, cancellationToken))
            .Where(static result => result is not null);

        // The plan is emitted per flow, and joined to the compilation's serialiser contexts
        // because an `.Emit` step's body is written through one. Combined rather than
        // collected on both sides: one flow changing still regenerates one file, and a
        // context changing regenerates the flows whose events it declares — which is
        // correct, since that is exactly when a dispatcher gains or loses a DescribeStep.
        context.RegisterSourceOutput(
            flows.Combine(jsonContexts.Collect()),
            static (production, data) => Produce(production, data.Left!, data.Right));

        context.RegisterSourceOutput(
            flows.Collect()
                .Combine(triggers.Collect())
                .Combine(jsonContexts.Collect())
                .Combine(httpAvailable)
                .Combine(application),
            static (production, data) =>
            {
                var ((((analysed, declared), contexts), available), assembly) = data;
                ProduceEndpoints(production, analysed, declared, contexts, available, assembly);
            });

        // Whether this compilation can register a schedule at all, expressed as one bool for the
        // reason httpAvailable is: nothing downstream re-runs when an unrelated reference
        // changes.
        var schedulingAvailable = context.CompilationProvider.Select(
            static (compilation, _) => compilation.GetTypeByMetadataName(ScheduleRegistrationName) is not null);

        context.RegisterSourceOutput(
            flows.Collect()
                .Combine(triggers.Collect())
                .Combine(schedulingAvailable)
                .Combine(application),
            static (production, data) =>
            {
                var (((analysed, declared), available), assembly) = data;
                ProduceSchedules(production, analysed, declared, available, assembly);
            });

        // Whether this compilation can register a subscription at all, expressed as one bool for
        // the reason httpAvailable is.
        var busAvailable = context.CompilationProvider.Select(
            static (compilation, _) => compilation.GetTypeByMetadataName(BusRegistrationName) is not null);

        context.RegisterSourceOutput(
            flows.Collect()
                .Combine(triggers.Collect())
                .Combine(busAvailable)
                .Combine(application),
            static (production, data) =>
            {
                var (((analysed, declared), available), assembly) = data;
                ProduceSubscriptions(production, analysed, declared, available, assembly);
            });

        // Whether this compilation can register a change subscription at all, expressed as one
        // bool for the reason httpAvailable is.
        var changeAvailable = context.CompilationProvider.Select(
            static (compilation, _) => compilation.GetTypeByMetadataName(ChangeRegistrationName) is not null);

        context.RegisterSourceOutput(
            flows.Collect()
                .Combine(triggers.Collect())
                .Combine(changeAvailable)
                .Combine(application),
            static (production, data) =>
            {
                var (((analysed, declared), available), assembly) = data;
                ProduceChangeSubscriptions(production, analysed, declared, available, assembly);
            });

        // Whether this compilation can register a stream subscription at all, expressed as one
        // bool for the reason httpAvailable is.
        var streamAvailable = context.CompilationProvider.Select(
            static (compilation, _) => compilation.GetTypeByMetadataName(StreamRegistrationName) is not null);

        context.RegisterSourceOutput(
            flows.Collect()
                .Combine(triggers.Collect())
                .Combine(streamAvailable)
                .Combine(application),
            static (production, data) =>
            {
                var (((analysed, declared), available), assembly) = data;
                ProduceStreamSubscriptions(production, analysed, declared, available, assembly);
            });

        // Whether this compilation has a container to register into, expressed as one bool for
        // the reason httpAvailable is.
        var containerAvailable = context.CompilationProvider.Select(
            static (compilation, _) =>
                compilation.GetTypeByMetadataName(ServiceCollectionExtensionsName) is not null);

        // Whether the host can be told what was declared. Separate from containerAvailable: a
        // project may have a container and not FlowX.Hosting, and a declaration it cannot check
        // would not compile.
        var declarationAvailable = context.CompilationProvider.Select(
            static (compilation, _) =>
                compilation.GetTypeByMetadataName(TriggerDeclarationName) is not null);

        context.RegisterSourceOutput(
            flows.Collect()
                .Combine(triggers.Collect())
                .Combine(containerAvailable)
                .Combine(declarationAvailable)
                .Combine(application),
            static (production, data) =>
            {
                var ((((analysed, declared), container), checkable), assembly) = data;
                ProduceCapabilityRegistrations(
                    production, analysed, declared, container, checkable, assembly);
            });

        // The one call that starts everything this assembly declared. Emitted from the same
        // predicates the individual registrations use, so it can never call one that was not
        // emitted or skip one that was.
        context.RegisterSourceOutput(
            flows.Collect()
                .Combine(triggers.Collect())
                .Combine(jsonContexts.Collect())
                .Combine(httpAvailable)
                .Combine(busAvailable.Combine(changeAvailable))
                .Combine(schedulingAvailable.Combine(streamAvailable))
                .Combine(application),
            static (production, data) =>
            {
                var ((((((analysed, declared), contexts), http), (bus, change)),
                    (schedule, stream)), assembly) = data;

                ProduceHostWiring(
                    production, analysed, declared, contexts, http, bus, change, schedule, stream,
                    assembly);
            });

        // Whether this compilation can bind an agent tool at all, expressed as one bool for the
        // reason httpAvailable is.
        var agentToolsAvailable = context.CompilationProvider.Select(
            static (compilation, _) => compilation.GetTypeByMetadataName(AgentToolRegistrationName) is not null);

        context.RegisterSourceOutput(
            flows.Collect()
                .Combine(triggers.Collect())
                .Combine(jsonContexts.Collect())
                .Combine(agentToolsAvailable)
                .Combine(application),
            static (production, data) =>
            {
                var ((((analysed, declared), contexts), available), assembly) = data;
                ProduceAgentTools(production, analysed, declared, contexts, available, assembly);
            });
    }

    /// <summary>
    /// Emits one binding per <c>[AgentTrigger]</c>, or nothing at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing at all is the common case: a project with no agent-triggered flow, or a flow
    /// library with no agent surface to register into, gets no file — zero types, zero IL.
    /// </para>
    /// <para>
    /// <strong>There is no <c>CanBeCalled</c> predicate here, unlike
    /// <see cref="ProduceSchedules"/>'s <c>CanBeFired</c> and
    /// <see cref="ProduceSubscriptions"/>'s <c>CanBeConsumed</c>, and the absence is the
    /// honest answer rather than an omission.</strong> A firing has no body and a delivery
    /// has only a message, so each constrains the flow's input contract and each has a
    /// diagnostic saying so. A <c>tools/call</c> carries an arbitrary JSON object, so any
    /// contract a serialiser can read is a contract an agent can supply — there is no
    /// condition to report and therefore no diagnostic to raise. Nor does an agent tool
    /// require <c>Durable</c>: nothing derives an instance id for it, because an agent's call
    /// is a request rather than a redelivery, so an <c>Ephemeral</c> flow is served exactly
    /// as an HTTP request serves one.
    /// </para>
    /// </remarks>
    private static void ProduceAgentTools(
        SourceProductionContext production,
        ImmutableArray<AnalysisResult?> results,
        ImmutableArray<FlowTriggersModel?> triggers,
        ImmutableArray<JsonContextModel?> contexts,
        bool agentToolsAvailable,
        string assemblyName)
    {
        if (!agentToolsAvailable)
        {
            return;
        }

        var declared = triggers
            .Where(static t => t is not null)
            .GroupBy(static t => t!.FlowId, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => g.First()!, StringComparer.Ordinal);

        var models = results
            .Where(static r => r is { IsSuccess: true, Model: not null })
            .Select(static r => r!.Model!)
            .OrderBy(static m => m.FlowId, StringComparer.Ordinal)
            .ToList();

        var serialisers = contexts.Where(static c => c is not null).Select(static c => c!).ToList();
        var tools = new List<AgentToolModel>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var flow in models)
        {
            if (!declared.TryGetValue(flow.FlowId, out var flowTriggers) ||
                !flowTriggers.Triggers.Any(IsAgentTool))
            {
                continue;
            }

            tools.Add(new AgentToolModel(
                flow.FlowId,
                flow.FullTypeName,
                AgentToolMethodName(flow.TypeName, names),
                flow.InputTypeName,
                flow.OutputTypeName,
                ContextFor(serialisers, flow.InputTypeName, flow.OutputTypeName)));
        }

        if (tools.Count > 0)
        {
            production.AddSource(
                AgentToolEmitter.FileName,
                SourceText.From(AgentToolEmitter.Emit(assemblyName, tools), Encoding.UTF8));
        }
    }

    /// <summary>An agent trigger, matched on kind alone.</summary>
    /// <remarks>
    /// On kind alone, where <see cref="IsHttpAddress"/>, <see cref="IsScheduleAddress"/> and
    /// <see cref="IsBusAddress"/> each additionally require the address they read to be
    /// present. An agent tool has no address to read — it is named by the flow's own id and
    /// described by the manifest — so a <c>[AgentTrigger]</c> whose arguments this build
    /// could not interpret still binds, and publishes a tool with no description rather than
    /// no tool at all. A missing description costs a model some context; a missing tool is a
    /// capability the application declared and nothing serves, which is the whole gap this
    /// closes.
    /// </remarks>
    private static bool IsAgentTool(TriggerModel trigger) =>
        string.Equals(trigger.Kind, "Agent", StringComparison.Ordinal);

    /// <summary>The extension method one agent tool is bound by.</summary>
    /// <remarks>
    /// Named from the flow's type for <see cref="MethodName"/>'s reason:
    /// <c>services.AddPlaceOrderFlowTool()</c> reads as the flow it binds. <c>[AgentTrigger]</c>
    /// is <c>AllowMultiple = false</c>, so the numeric suffix is reachable only through two
    /// flows of the same type name in different namespaces.
    /// </remarks>
    private static string AgentToolMethodName(string typeName, HashSet<string> taken)
    {
        var candidate = "Add" + typeName + "Tool";
        var suffix = 2;

        while (!taken.Add(candidate))
        {
            candidate = "Add" + typeName + "Tool" + suffix.ToString(CultureInfo.InvariantCulture);
            suffix++;
        }

        return candidate;
    }

    /// <summary>
    /// Emits the container registration for every capability a dispatcher takes, or nothing at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing at all when the compilation has no container — a flow library referencing only
    /// <c>FlowX.Abstractions</c> is a legitimate shape and keeps compiling.
    /// </para>
    /// <para>
    /// Every flow that analysed successfully contributes its dispatcher, including one no trigger
    /// reaches: a composed sub-flow's dispatcher is a constructor parameter of its parent's, and a
    /// test resolves one directly.
    /// </para>
    /// </remarks>
    private static void ProduceCapabilityRegistrations(
        SourceProductionContext production,
        ImmutableArray<AnalysisResult?> results,
        ImmutableArray<FlowTriggersModel?> triggers,
        bool containerAvailable,
        bool declarationAvailable,
        string assemblyName)
    {
        if (!containerAvailable)
        {
            return;
        }

        var models = results
            .Where(static r => r is { IsSuccess: true, Model: not null })
            .Select(static r => r!.Model!)
            .ToList();

        var capabilities = models
            .SelectMany(static m => m.ReferencedCapabilities)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToList();

        var dispatchers = models
            .Select(static m => m.FullTypeName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToList();

        if (capabilities.Count == 0 && dispatchers.Count == 0)
        {
            return;
        }

        production.AddSource(
            CapabilityRegistrationEmitter.FileName,
            SourceText.From(
                CapabilityRegistrationEmitter.Emit(
                    assemblyName,
                    capabilities,
                    dispatchers,
                    declarationAvailable ? DeclarationsOf(models, triggers) : []),
                Encoding.UTF8));
    }

    /// <summary>
    /// The addresses the host is expected to serve, under exactly the predicates that decide
    /// whether a registration is emitted for them.
    /// </summary>
    /// <remarks>
    /// The predicates are shared with <see cref="ProduceSubscriptions"/> and its three siblings on
    /// purpose. A declaration produced by a looser rule than the registration would refuse to
    /// start a host over an address the generator itself had decided not to serve — a check that
    /// fails on correct code, which is worse than the hole it was added to close.
    /// </remarks>
    private static List<DeclaredTriggerModel> DeclarationsOf(
        List<FlowModel> models,
        ImmutableArray<FlowTriggersModel?> triggers)
    {
        var declared = triggers
            .Where(static t => t is not null)
            .GroupBy(static t => t!.FlowId, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => g.First()!, StringComparer.Ordinal);

        var declarations = new List<DeclaredTriggerModel>();

        foreach (var flow in models.OrderBy(static m => m.FlowId, StringComparer.Ordinal))
        {
            if (!declared.TryGetValue(flow.FlowId, out var flowTriggers))
            {
                continue;
            }

            foreach (var trigger in flowTriggers.Triggers)
            {
                if (IsBusAddress(trigger) && CanBeStartedByADelivery(flow, flowTriggers))
                {
                    declarations.Add(new DeclaredTriggerModel(
                        flow.FlowId, flow.Version, "Bus", trigger.Topic!));
                }
                else if (IsChangeAddress(trigger) && CanBeConsumed(flow))
                {
                    declarations.Add(new DeclaredTriggerModel(
                        flow.FlowId, flow.Version, "Change", trigger.Topic!));
                }
                else if (IsScheduleAddress(trigger) && CanBeFired(flow))
                {
                    declarations.Add(new DeclaredTriggerModel(
                        flow.FlowId, flow.Version, "Schedule", trigger.Cron!));
                }
                else if (IsStreamAddress(trigger) && CanBeWindowed(flow)
                    && WindowOf(flowTriggers, trigger) is { } window
                    && IsWindowShapeThisEngineImplements(window))
                {
                    declarations.Add(new DeclaredTriggerModel(
                        flow.FlowId, flow.Version, "Stream", trigger.Topic!));
                }
            }
        }

        return declarations;
    }

    /// <summary>
    /// Emits the one call that starts everything this assembly declared, or nothing at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing at all when the assembly declares no address the host has to be told about — a
    /// library of flows other applications compose has no wiring of its own.
    /// </para>
    /// <para>
    /// <strong>Both the declaration and the reference have to be true for a call to be
    /// emitted.</strong> The individual registration classes are emitted only when the flows
    /// declare that kind <em>and</em> the hosting type is referenced, so calling one under a
    /// looser rule would not compile — which is why this recomputes both rather than assuming
    /// either.
    /// </para>
    /// </remarks>
    private static void ProduceHostWiring(
        SourceProductionContext production,
        ImmutableArray<AnalysisResult?> results,
        ImmutableArray<FlowTriggersModel?> triggers,
        ImmutableArray<JsonContextModel?> jsonContexts,
        bool httpAvailable,
        bool busAvailable,
        bool changeAvailable,
        bool scheduleAvailable,
        bool streamAvailable,
        string assemblyName)
    {
        var models = results
            .Where(static r => r is { IsSuccess: true, Model: not null })
            .Select(static r => r!.Model!)
            .ToList();

        var declared = DeclarationsOf(models, triggers);

        var model = new HostWiringModel(
            Endpoints: httpAvailable && HasMappableEndpoint(models, triggers, jsonContexts),
            Bus: busAvailable && declared.Any(static d => d.Kind == "Bus"),
            Change: changeAvailable && declared.Any(static d => d.Kind == "Change"),
            Schedules: scheduleAvailable && declared.Any(static d => d.Kind == "Schedule"),
            Streams: streamAvailable && declared.Any(static d => d.Kind == "Stream"));

        if (!model.Any)
        {
            return;
        }

        production.AddSource(
            HostWiringEmitter.FileName,
            SourceText.From(HostWiringEmitter.Emit(assemblyName, model), Encoding.UTF8));
    }

    /// <summary>
    /// Whether <c>MapFlowX()</c> — the form that takes no serialiser — exists to be called.
    /// </summary>
    /// <remarks>
    /// <strong>Both halves, and the second is the one that bites.</strong> The no-argument
    /// overload is emitted only when <em>every</em> endpoint resolved a serialiser context of its
    /// own; where one did not, the only overload takes a <c>JsonSerializerContext</c> the caller
    /// has to name. An aggregate that assumed the first would emit a generated file that does not
    /// compile — which is how a project with no <c>[JsonSerializable]</c> context found it.
    /// </remarks>
    private static bool HasMappableEndpoint(
        List<FlowModel> models,
        ImmutableArray<FlowTriggersModel?> triggers,
        ImmutableArray<JsonContextModel?> jsonContexts)
    {
        var declared = triggers
            .Where(static t => t is not null)
            .GroupBy(static t => t!.FlowId, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => g.First()!, StringComparer.Ordinal);

        var contexts = jsonContexts
            .Where(static c => c is not null)
            .Select(static c => c!)
            .ToList();

        var routed = models
            .Where(flow =>
                flow.ReturnProjection is not null
                && declared.TryGetValue(flow.FlowId, out var flowTriggers)
                && flowTriggers.Triggers.Any(IsHttpAddress))
            .ToList();

        return routed.Count > 0
            && routed.All(flow =>
                ContextFor(contexts, flow.InputTypeName, flow.OutputTypeName) is not null);
    }

    /// <summary>The window a stream trigger declared, or null when it named none.</summary>
    private static string? WindowOf(FlowTriggersModel flowTriggers, TriggerModel trigger) =>
        flowTriggers.Streams
            .FirstOrDefault(s => string.Equals(s.Source, trigger.Topic, StringComparison.Ordinal))
            ?.Window;

    /// <summary>
    /// Emits one registration per bus trigger the host could actually consume, or nothing at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing at all is the common case: a project with no flow that subscribes, or a flow
    /// library with no host to register into, gets no file — zero types, zero IL.
    /// </para>
    /// <para>
    /// <strong>A flow this host could not start is skipped here and reported by
    /// <c>TriggerDeclarationAnalyzer</c>, not by both</strong>, for <see cref="ProduceSchedules"/>'s
    /// reason: the two conditions are the same two <c>FLOWX1039</c> names, and the analyzer has the
    /// attribute's own span where this has a collected model and nothing else.
    /// </para>
    /// </remarks>
    private static void ProduceSubscriptions(
        SourceProductionContext production,
        ImmutableArray<AnalysisResult?> results,
        ImmutableArray<FlowTriggersModel?> triggers,
        bool busAvailable,
        string assemblyName)
    {
        if (!busAvailable)
        {
            return;
        }

        var declared = triggers
            .Where(static t => t is not null)
            .GroupBy(static t => t!.FlowId, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => g.First()!, StringComparer.Ordinal);

        var models = results
            .Where(static r => r is { IsSuccess: true, Model: not null })
            .Select(static r => r!.Model!)
            .OrderBy(static m => m.FlowId, StringComparer.Ordinal)
            .ToList();

        var subscriptions = new List<BusSubscriptionModel>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var flow in models)
        {
            if (!declared.TryGetValue(flow.FlowId, out var flowTriggers)
                || !CanBeStartedByADelivery(flow, flowTriggers))
            {
                continue;
            }

            foreach (var trigger in flowTriggers.Triggers.Where(IsBusAddress))
            {
                subscriptions.Add(new BusSubscriptionModel(
                    flow.FlowId,
                    flow.FullTypeName,
                    SubscriptionMethodName(flow.TypeName, names),
                    trigger.Topic!,
                    trigger.Group!,
                    trigger.Transport,
                    trigger.Decoder,
                    trigger.Decoder is null ? null : flow.InputTypeName));
            }
        }

        if (subscriptions.Count > 0)
        {
            production.AddSource(
                BusEmitter.FileName,
                SourceText.From(BusEmitter.Emit(assemblyName, subscriptions), Encoding.UTF8));
        }
    }

    /// <summary>
    /// Emits one registration per <c>[ChangeTrigger]</c> the host can actually observe, or
    /// nothing at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ProduceSubscriptions"/>'s shape and its reasons, one transport over. Nothing at
    /// all is the common case: a project with no change-triggered flow, or a flow library with no
    /// host to register into, gets no file — zero types, zero IL.
    /// </para>
    /// <para>
    /// <strong>The two conditions are reported by <c>TriggerDeclarationAnalyzer</c>, not by
    /// both</strong>, for <see cref="ProduceSubscriptions"/>'s reason: they are the same two
    /// <c>FLOWX1041</c> names, and the analyzer has the attribute's own span where this has a
    /// collected model and nothing to point at. They are also, exactly,
    /// <see cref="CanBeConsumed"/> — a change and a delivery hand the flow the same
    /// <c>BusMessage</c> and need the same journal — which is why one predicate serves both.
    /// </para>
    /// <para>
    /// <strong>The third condition — a flow that observes a type it emits — is not checked
    /// here.</strong> It is refused at registration by <c>FlowChangeCatalog.Add</c>, which reads
    /// the emitted types off the <c>ExecutionPlan</c> that will actually run (ADR-0047).
    /// </para>
    /// </remarks>
    private static void ProduceChangeSubscriptions(
        SourceProductionContext production,
        ImmutableArray<AnalysisResult?> results,
        ImmutableArray<FlowTriggersModel?> triggers,
        bool changeAvailable,
        string assemblyName)
    {
        if (!changeAvailable)
        {
            return;
        }

        var declared = triggers
            .Where(static t => t is not null)
            .GroupBy(static t => t!.FlowId, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => g.First()!, StringComparer.Ordinal);

        var models = results
            .Where(static r => r is { IsSuccess: true, Model: not null })
            .Select(static r => r!.Model!)
            .OrderBy(static m => m.FlowId, StringComparer.Ordinal)
            .ToList();

        var subscriptions = new List<ChangeSubscriptionModel>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var flow in models)
        {
            if (!declared.TryGetValue(flow.FlowId, out var flowTriggers) || !CanBeConsumed(flow))
            {
                continue;
            }

            foreach (var trigger in flowTriggers.Triggers.Where(IsChangeAddress))
            {
                subscriptions.Add(new ChangeSubscriptionModel(
                    flow.FlowId,
                    flow.FullTypeName,
                    ChangeSubscriptionMethodName(flow.TypeName, names),
                    trigger.Topic!,
                    trigger.Group!));
            }
        }

        if (subscriptions.Count > 0)
        {
            production.AddSource(
                ChangeEmitter.FileName,
                SourceText.From(ChangeEmitter.Emit(assemblyName, subscriptions), Encoding.UTF8));
        }
    }

    /// <summary>A change trigger this build could read an address off.</summary>
    /// <remarks>
    /// Matched on kind and shape rather than on which attribute was written, for
    /// <see cref="IsBusAddress"/>'s reason. A trigger whose kind is <c>Change</c> but whose
    /// arguments this compiler could not interpret reaches the manifest as a bare kind and must
    /// produce no registration: a source nobody read is not a source to observe.
    /// </remarks>
    private static bool IsChangeAddress(TriggerModel trigger) =>
        string.Equals(trigger.Kind, "Change", StringComparison.Ordinal) &&
        !string.IsNullOrEmpty(trigger.Topic) &&
        !string.IsNullOrEmpty(trigger.Group);

    /// <summary>The extension method one change subscription is registered by.</summary>
    /// <remarks>
    /// Named from the flow's type for <see cref="SubscriptionMethodName"/>'s reason, and counted
    /// in a name set of its own because the two files are separate classes: a flow declaring both
    /// a bus trigger and a change trigger gets one method in each without either taking a suffix.
    /// </remarks>
    private static string ChangeSubscriptionMethodName(string typeName, HashSet<string> taken)
    {
        var candidate = "Add" + typeName + "ChangeSubscription";
        var suffix = 2;

        while (!taken.Add(candidate))
        {
            candidate = "Add" + typeName + "ChangeSubscription" +
                        suffix.ToString(CultureInfo.InvariantCulture);
            suffix++;
        }

        return candidate;
    }

    /// <summary>Whether a delivery or a change to this flow could be started, and started once.</summary>
    /// <remarks>
    /// The two conditions <c>FLOWX1039</c> reports, restated as a predicate: a delivery has only
    /// the message to hand over, and an ephemeral flow journals no instance, so nothing would
    /// refuse a redelivery of the same message. <c>FLOWX1041</c> names the same two conditions
    /// for a change, which is why <see cref="ProduceChangeSubscriptions"/> reads this one rather
    /// than a copy of it.
    /// </remarks>
    private static bool CanBeConsumed(FlowModel flow) =>
        string.Equals(flow.InputTypeName, "FlowX.BusMessage", StringComparison.Ordinal) &&
        string.Equals(flow.Profile, "Durable", StringComparison.Ordinal);

    /// <summary>
    /// Whether a delivery can start this flow: it takes the delivery, or every bus trigger on it
    /// names a decoder that turns one into what it does take.
    /// </summary>
    /// <remarks>
    /// Requiring the class to be declared over the delivery is what stopped one flow serving a
    /// bus and a schedule at once — the two payloads disagree and a class has one input type. A
    /// decoder meets the requirement without the class giving up its own contract, and whether
    /// the named type can actually do it is <c>FLOWX1051</c>'s question, asked where the
    /// attribute's own span is.
    /// </remarks>
    private static bool CanBeStartedByADelivery(FlowModel flow, FlowTriggersModel triggers) =>
        string.Equals(flow.Profile, "Durable", StringComparison.Ordinal)
        && (string.Equals(flow.InputTypeName, "FlowX.BusMessage", StringComparison.Ordinal)
            || triggers.Triggers.Where(IsBusAddress).All(trigger => trigger.Decoder is not null));

    /// <summary>
    /// Emits one registration per <c>[StreamTrigger]</c> the host can actually read, or nothing
    /// at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ProduceChangeSubscriptions"/>'s shape and its reasons. The three conditions are
    /// reported by <c>TriggerDeclarationAnalyzer</c> as <c>FLOWX1042</c> rather than here, for
    /// that method's reason: the analyzer has the attribute's own span.
    /// </para>
    /// <para>
    /// <strong>The window shape is one of the three, which is what makes this different from the
    /// other transports.</strong> A bus or a change registration is refused for a property of the
    /// <em>flow</em>; a stream registration is also refused for a property of the
    /// <em>declaration</em> — <c>Window = "session:5m"</c> names a shape this engine does not
    /// implement. Emitting a registration for it and letting <c>FlowStreamCatalog.Add</c> throw
    /// at startup would turn a build error into a deployment failure.
    /// </para>
    /// </remarks>
    private static void ProduceStreamSubscriptions(
        SourceProductionContext production,
        ImmutableArray<AnalysisResult?> results,
        ImmutableArray<FlowTriggersModel?> triggers,
        bool streamAvailable,
        string assemblyName)
    {
        if (!streamAvailable)
        {
            return;
        }

        var declared = triggers
            .Where(static t => t is not null)
            .GroupBy(static t => t!.FlowId, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => g.First()!, StringComparer.Ordinal);

        var models = results
            .Where(static r => r is { IsSuccess: true, Model: not null })
            .Select(static r => r!.Model!)
            .OrderBy(static m => m.FlowId, StringComparer.Ordinal)
            .ToList();

        var subscriptions = new List<StreamSubscriptionModel>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var flow in models)
        {
            if (!declared.TryGetValue(flow.FlowId, out var flowTriggers) || !CanBeWindowed(flow))
            {
                continue;
            }

            foreach (var trigger in flowTriggers.Triggers.Where(IsStreamAddress))
            {
                var stream = flowTriggers.Streams
                    .FirstOrDefault(s => string.Equals(s.Source, trigger.Topic, StringComparison.Ordinal));

                if (stream is null || !IsWindowShapeThisEngineImplements(stream.Window))
                {
                    continue;
                }

                subscriptions.Add(new StreamSubscriptionModel(
                    flow.FlowId,
                    flow.FullTypeName,
                    StreamSubscriptionMethodName(flow.TypeName, names),
                    trigger.Topic!,
                    stream.Window,
                    stream.Lateness,
                    stream.Checkpoint,
                    stream.Parallelism));
            }
        }

        if (subscriptions.Count > 0)
        {
            production.AddSource(
                StreamEmitter.FileName,
                SourceText.From(StreamEmitter.Emit(assemblyName, subscriptions), Encoding.UTF8));
        }
    }

    /// <summary>A flow a closed window can start.</summary>
    /// <remarks>
    /// <see cref="CanBeConsumed"/>'s two conditions with both terms changed. A window hands the
    /// flow an interval and its records, so the input must be <c>StreamWindowBatch</c>; and the
    /// checkpoint is committed after the window's flow has run, so only a journaled instance has
    /// a primary key to refuse the rebuild a crash forces — which for a stream is the
    /// <c>Streaming</c> profile specifically, because that is the one line that says this flow's
    /// input is a window.
    /// </remarks>
    private static bool CanBeWindowed(FlowModel flow) =>
        string.Equals(flow.InputTypeName, "FlowX.StreamWindowBatch", StringComparison.Ordinal) &&
        string.Equals(flow.Profile, "Streaming", StringComparison.Ordinal);

    /// <summary>A stream trigger this build could read an address off.</summary>
    private static bool IsStreamAddress(TriggerModel trigger) =>
        string.Equals(trigger.Kind, "Stream", StringComparison.Ordinal) &&
        !string.IsNullOrEmpty(trigger.Topic);

    /// <summary>
    /// Whether the declared window is the one shape the stream engine implements.
    /// </summary>
    /// <remarks>
    /// <strong>A prefix test, and deliberately weaker than <c>StreamWindowSpec.Read</c>.</strong>
    /// This assembly is netstandard2.0 and references no runtime assembly, so the real reader is
    /// not callable here and duplicating it would be a second parser to keep in step. What this
    /// has to get right is the half that decides whether a registration is emitted at all —
    /// which shape family was named — and <c>tumbling:</c> is that whole question. A malformed
    /// duration inside a tumbling window still reaches <c>FlowStreamCatalog.Add</c>, which
    /// refuses it at startup with the reader's own message.
    /// </remarks>
    private static bool IsWindowShapeThisEngineImplements(string window) =>
        window.StartsWith("tumbling:", StringComparison.Ordinal);

    /// <summary>The extension method one stream subscription is registered by.</summary>
    private static string StreamSubscriptionMethodName(string typeName, HashSet<string> taken)
    {
        var candidate = "Add" + typeName + "StreamSubscription";
        var suffix = 2;

        while (!taken.Add(candidate))
        {
            candidate = "Add" + typeName + "StreamSubscription" +
                        suffix.ToString(CultureInfo.InvariantCulture);
            suffix++;
        }

        return candidate;
    }

    /// <summary>A bus trigger this build could read an address off.</summary>
    /// <remarks>
    /// <strong>Matched on kind and shape rather than on which attribute was written</strong>, so
    /// <c>[BusTrigger]</c> and <c>[KafkaTrigger]</c> bind through one path — which is ADR-0004's
    /// "one trigger abstraction for every transport" expressed as code rather than as a claim. A
    /// trigger whose kind is <c>Bus</c> but whose arguments this compiler could not interpret
    /// reaches the manifest as a bare kind and must produce no registration, for the reason an
    /// unreadable schedule produces none: a topic nobody read is not a topic to consume.
    /// </remarks>
    private static bool IsBusAddress(TriggerModel trigger) =>
        string.Equals(trigger.Kind, "Bus", StringComparison.Ordinal) &&
        !string.IsNullOrEmpty(trigger.Topic) &&
        !string.IsNullOrEmpty(trigger.Group);

    /// <summary>The extension method one subscription is registered by.</summary>
    /// <remarks>
    /// Named from the flow's type for <see cref="MethodName"/>'s reason:
    /// <c>services.AddRepriceOrderFlowSubscription()</c> reads as the flow it registers. A flow
    /// declaring two subscriptions takes a numeric suffix, deterministically, in the order the
    /// flows were sorted by id.
    /// </remarks>
    private static string SubscriptionMethodName(string typeName, HashSet<string> taken)
    {
        var candidate = "Add" + typeName + "Subscription";
        var suffix = 2;

        while (!taken.Add(candidate))
        {
            candidate = "Add" + typeName + "Subscription" + suffix.ToString(CultureInfo.InvariantCulture);
            suffix++;
        }

        return candidate;
    }

    /// <summary>
    /// Emits one registration per <c>[CronTrigger]</c> the host can actually fire, or nothing
    /// at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing at all is the common case: a project with no flow that declares a schedule, or a
    /// flow library with no host to register into, gets no file — zero types, zero IL.
    /// </para>
    /// <para>
    /// <strong>A flow this host could not fire is skipped here and reported by
    /// <c>TriggerDeclarationAnalyzer</c>, not by both.</strong> The two conditions are the same
    /// two <c>FLOWX1038</c> names — an input contract that is not <c>ScheduledFire</c>, and a
    /// profile that is not <c>Durable</c> — and the analyzer has the attribute's own span to
    /// point at where this has a collected model and nothing else. Reporting from both would put
    /// the same defect in the build log twice, in one case with no file name.
    /// </para>
    /// </remarks>
    private static void ProduceSchedules(
        SourceProductionContext production,
        ImmutableArray<AnalysisResult?> results,
        ImmutableArray<FlowTriggersModel?> triggers,
        bool schedulingAvailable,
        string assemblyName)
    {
        if (!schedulingAvailable)
        {
            return;
        }

        var declared = triggers
            .Where(static t => t is not null)
            .GroupBy(static t => t!.FlowId, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => g.First()!, StringComparer.Ordinal);

        var models = results
            .Where(static r => r is { IsSuccess: true, Model: not null })
            .Select(static r => r!.Model!)
            .OrderBy(static m => m.FlowId, StringComparer.Ordinal)
            .ToList();

        var schedules = new List<ScheduleModel>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var flow in models)
        {
            if (!declared.TryGetValue(flow.FlowId, out var flowTriggers) || !CanBeFired(flow))
            {
                continue;
            }

            foreach (var trigger in flowTriggers.Triggers.Where(IsScheduleAddress))
            {
                schedules.Add(new ScheduleModel(
                    flow.FlowId,
                    flow.FullTypeName,
                    ScheduleMethodName(flow.TypeName, names),
                    trigger.Cron!,
                    trigger.TimeZone ?? "UTC",
                    MissedFireFor(flowTriggers, trigger.Cron!),
                    PerTenantFor(flowTriggers, trigger.Cron!),
                    OverlapFor(flowTriggers, trigger.Cron!),
                    JitterFor(flowTriggers, trigger.Cron!)));
            }
        }

        if (schedules.Count > 0)
        {
            production.AddSource(
                ScheduleEmitter.FileName,
                SourceText.From(ScheduleEmitter.Emit(assemblyName, schedules), Encoding.UTF8));
        }
    }

    /// <summary>Whether a firing of this flow could be started, and started once.</summary>
    /// <remarks>
    /// The two conditions <c>FLOWX1038</c> reports, restated as a predicate: a cron firing has no
    /// body, so the flow has to bind the occurrence; and an ephemeral flow journals no instance,
    /// so nothing would refuse a second node's firing of the same occurrence.
    /// </remarks>
    private static bool CanBeFired(FlowModel flow) =>
        string.Equals(flow.InputTypeName, "FlowX.ScheduledFire", StringComparison.Ordinal) &&
        string.Equals(flow.Profile, "Durable", StringComparison.Ordinal);

    /// <summary>A schedule trigger this build could read an expression off.</summary>
    /// <remarks>
    /// A trigger whose kind is <c>Schedule</c> but whose arguments this compiler could not
    /// interpret reaches the manifest as a bare kind, and must produce no registration for the
    /// same reason it produces no endpoint: an expression nobody read is not a schedule to fire.
    /// </remarks>
    private static bool IsScheduleAddress(TriggerModel trigger) =>
        string.Equals(trigger.Kind, "Schedule", StringComparison.Ordinal) &&
        !string.IsNullOrEmpty(trigger.Cron);

    /// <summary>
    /// The missed-fire policy declared beside this expression, or the attribute's own default.
    /// </summary>
    /// <remarks>
    /// Joined on the expression rather than on position, because <c>FlowTriggersModel</c> sorts
    /// its triggers ordinally so the manifest is byte-stable and the declarations are in source
    /// order. A flow declaring the same expression twice gets the first policy for both, which is
    /// a declaration nobody should write and which fires one schedule either way — the two
    /// registrations collapse onto one key in <c>FlowScheduleCatalog</c>.
    /// </remarks>
    private static string MissedFireFor(FlowTriggersModel triggers, string cron) => triggers.Schedules
        .Where(schedule => string.Equals(schedule.Cron, cron, StringComparison.Ordinal))
        .Select(static schedule => schedule.MissedFire)
        .FirstOrDefault() ?? "RunOnce";

    /// <summary>Whether the declaration beside this expression asked for a per-tenant fan-out.</summary>
    /// <remarks><see cref="MissedFireFor"/>'s join, for the other property read off the attribute.</remarks>
    private static bool PerTenantFor(FlowTriggersModel triggers, string cron) => triggers.Schedules
        .Where(schedule => string.Equals(schedule.Cron, cron, StringComparison.Ordinal))
        .Select(static schedule => schedule.PerTenant)
        .FirstOrDefault();

    /// <summary>The overlap policy declared beside this expression, or the attribute's default.</summary>
    /// <remarks><see cref="MissedFireFor"/>'s join, for the third property read off the attribute.</remarks>
    private static string OverlapFor(FlowTriggersModel triggers, string cron) => triggers.Schedules
        .Where(schedule => string.Equals(schedule.Cron, cron, StringComparison.Ordinal))
        .Select(static schedule => schedule.Overlap)
        .FirstOrDefault() ?? "Skip";

    /// <summary>The spread declared beside this expression, verbatim, or null.</summary>
    /// <remarks><see cref="MissedFireFor"/>'s join, for the fourth property read off the attribute.</remarks>
    private static string? JitterFor(FlowTriggersModel triggers, string cron) => triggers.Schedules
        .Where(schedule => string.Equals(schedule.Cron, cron, StringComparison.Ordinal))
        .Select(static schedule => schedule.Jitter)
        .FirstOrDefault();

    /// <summary>The extension method one schedule is registered by.</summary>
    /// <remarks>
    /// Named from the flow's type for <see cref="MethodName"/>'s reason:
    /// <c>services.AddReconcileLedgerFlowSchedule()</c> reads as the flow it registers. A flow
    /// declaring two schedules takes a numeric suffix, deterministically, in the order the flows
    /// were sorted by id.
    /// </remarks>
    private static string ScheduleMethodName(string typeName, HashSet<string> taken)
    {
        var candidate = "Add" + typeName + "Schedule";
        var suffix = 2;

        while (!taken.Add(candidate))
        {
            candidate = "Add" + typeName + "Schedule" + suffix.ToString(CultureInfo.InvariantCulture);
            suffix++;
        }

        return candidate;
    }

    /// <summary>
    /// Reads one <c>JsonSerializerContext</c> and the contracts it declares.
    /// </summary>
    /// <remarks>
    /// Anything that is not a serialiser context, or that generated code in this assembly
    /// could not name, is dropped here rather than filtered later — a context nested
    /// inside a private type is not a candidate for anything, and letting it reach the
    /// emitter would only give the emitter a rule to restate.
    /// </remarks>
    private static JsonContextModel? ReadJsonContext(
        GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context.TargetSymbol is not INamedTypeSymbol symbol || !IsJsonSerializerContext(symbol) ||
            !IsVisibleInAssembly(symbol))
        {
            return null;
        }

        var contracts = context.Attributes
            .Where(static a => a.ConstructorArguments.Length > 0)
            .Select(static a => a.ConstructorArguments[0].Value as INamedTypeSymbol)
            .Where(static t => t is not null)
            .Select(static t => Display(t!))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToList();

        return contracts.Count == 0 ? null : new JsonContextModel(Display(symbol), contracts);
    }

    private static bool IsJsonSerializerContext(INamedTypeSymbol symbol)
    {
        for (var type = symbol.BaseType; type is not null; type = type.BaseType)
        {
            if (string.Equals(type.ToDisplayString(), JsonSerializerContextName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether other code in the same assembly can name this type.</summary>
    private static bool IsVisibleInAssembly(INamedTypeSymbol symbol)
    {
        for (var type = symbol; type is not null; type = type.ContainingType)
        {
            if (type.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The fully qualified name of a symbol, in the form emitted source uses.</summary>
    private static string Display(ISymbol symbol) => symbol
        .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
        .Replace("global::", string.Empty);

    /// <summary>
    /// Emits one endpoint registration per <c>[HttpTrigger]</c>, or nothing at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing at all is the common case and the important one. A project with no HTTP
    /// transport, or with no flow that declares an address on it, gets no file — so the
    /// cost of this feature to a Kafka-only application is zero types, zero IL and zero
    /// build output, which is the property that lets a transport be a plugin.
    /// </para>
    /// <para>
    /// A flow with an <c>[HttpTrigger]</c> but no <c>.Return(...)</c> is skipped: the
    /// endpoint writes the flow's declared output, and a flow that declares none has no
    /// <c>Projection</c> field to write it with.
    /// </para>
    /// </remarks>
    private static void ProduceEndpoints(
        SourceProductionContext production,
        ImmutableArray<AnalysisResult?> results,
        ImmutableArray<FlowTriggersModel?> triggers,
        ImmutableArray<JsonContextModel?> jsonContexts,
        bool httpAvailable,
        string assemblyName)
    {
        if (!httpAvailable)
        {
            return;
        }

        var declared = triggers
            .Where(static t => t is not null)
            .GroupBy(static t => t!.FlowId, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => g.First()!, StringComparer.Ordinal);

        var contexts = jsonContexts
            .Where(static c => c is not null)
            .Select(static c => c!)
            .ToList();

        var models = results
            .Where(static r => r is { IsSuccess: true, Model: not null })
            .Select(static r => r!.Model!)
            .OrderBy(static m => m.FlowId, StringComparer.Ordinal)
            .ToList();

        var endpoints = new List<HttpEndpointModel>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var flow in models)
        {
            if (flow.ReturnProjection is null ||
                !declared.TryGetValue(flow.FlowId, out var flowTriggers))
            {
                continue;
            }

            foreach (var trigger in flowTriggers.Triggers.Where(IsHttpAddress))
            {
                endpoints.Add(new HttpEndpointModel(
                    flow.FlowId,
                    flow.FullTypeName,
                    MethodName(flow.TypeName, names),
                    flow.InputTypeName,
                    flow.OutputTypeName,
                    trigger.Method!,
                    trigger.Route!,
                    trigger.Idempotent == true,
                    ContextFor(contexts, flow.InputTypeName, flow.OutputTypeName),
                    SignalsOf(flow)));
            }
        }

        if (endpoints.Count > 0)
        {
            production.AddSource(
                EndpointEmitter.FileName,
                SourceText.From(EndpointEmitter.Emit(assemblyName, endpoints), Encoding.UTF8));
        }
    }

    /// <summary>
    /// Every signal a flow can suspend at, deduplicated by identity in step order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Read from the flow's body, which is the one field of
    /// <see cref="HttpEndpointModel"/> that does not come from the trigger attribute.</strong>
    /// A wait is declared in <c>Define</c>, and that is where this reads it, so a route serving
    /// a signal nothing waits for cannot be generated — see
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0022-http-shape-of-a-suspending-flow.md">ADR-0022</a>,
    /// rejected option F.
    /// </para>
    /// <para>
    /// <c>AllSteps</c> rather than <c>Steps</c>, so a wait inside a conditional or a loop body
    /// gets its route: it is still a wait the flow can stop at, and a sender delivering to it
    /// does not know which branch put it there. A wait whose contract this compilation could
    /// not resolve produces no route, for the reason an unreadable trigger produces no
    /// endpoint — the emitted call needs a type to name.
    /// </para>
    /// </remarks>
    private static List<SignalEndpointModel> SignalsOf(FlowModel flow)
    {
        var signals = new List<SignalEndpointModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var step in flow.AllSteps)
        {
            // A poll's `.OrSignal<T>()` is a wait the flow can be continued at in exactly the
            // sense a suspension point is — the instance is parked, and a delivery to that
            // identity ends the wait — so it gets the same route. Distinguishing the two here
            // would be publishing an address for one kind of parked instance and not the other.
            if (step.Kind is StepKindModel.AwaitSignal or StepKindModel.Poll &&
                step.SignalType is { Length: > 0 } signal &&
                step.SignalContractTypeName is { Length: > 0 } contract &&
                seen.Add(signal))
            {
                signals.Add(new SignalEndpointModel(signal, contract));
            }
        }

        return signals;
    }

    /// <summary>An HTTP trigger this build could read an address off.</summary>
    /// <remarks>
    /// A trigger whose kind is <c>Http</c> but whose arguments this compiler could not
    /// interpret reaches the manifest as a bare kind, and it must produce no endpoint for
    /// the same reason: a route nobody read is not a route to serve.
    /// </remarks>
    private static bool IsHttpAddress(TriggerModel trigger) =>
        string.Equals(trigger.Kind, "Http", StringComparison.Ordinal) &&
        !string.IsNullOrEmpty(trigger.Method) &&
        !string.IsNullOrEmpty(trigger.Route);

    /// <summary>
    /// The single serialiser context declaring both contracts, or <c>null</c>.
    /// </summary>
    /// <remarks>
    /// <c>null</c> for none and for several, and the two are the same answer: the
    /// no-argument overload exists only where there is nothing to choose. Picking the
    /// first of several would make the wire format depend on file order.
    /// </remarks>
    private static string? ContextFor(List<JsonContextModel> contexts, string input, string output)
    {
        string? found = null;

        foreach (var candidate in contexts.Where(c => c.Declares(input, output)))
        {
            if (found is not null)
            {
                return null;
            }

            found = candidate.TypeName;
        }

        return found;
    }

    /// <summary>
    /// The extension method one endpoint is registered by.
    /// </summary>
    /// <remarks>
    /// Named from the flow's type rather than from its id, because this is a C# member a
    /// developer types: <c>app.MapPlaceOrderFlow()</c> reads as the flow it maps, where
    /// <c>MapOrderPlace()</c> would need the id in front of you. Two flows of the same
    /// type name in different namespaces, or one flow declaring two addresses, take a
    /// numeric suffix in the order the flows were sorted by id — deterministic, and rare
    /// enough that the alternative of mangling every name is the wrong trade.
    /// </remarks>
    private static string MethodName(string typeName, HashSet<string> taken)
    {
        var candidate = "Map" + typeName;
        var suffix = 2;

        while (!taken.Add(candidate))
        {
            candidate = "Map" + typeName + suffix.ToString(CultureInfo.InvariantCulture);
            suffix++;
        }

        return candidate;
    }

    /// <summary>Reads the trigger attributes off one flow declaration.</summary>
    /// <remarks>
    /// The flow id is read from <c>[Flow]</c> here rather than taken from the analysed
    /// model, because this pipeline runs independently of that one: a trigger set that
    /// waited for a flow to analyse cleanly would vanish from the manifest whenever the
    /// flow body had an unrelated error, which is precisely when a reviewer is looking.
    /// </remarks>
    private static FlowTriggersModel? ReadTriggers(
        GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context.TargetSymbol is not INamedTypeSymbol symbol)
        {
            return null;
        }

        var declared = TriggerReader.Read(symbol);

        if (declared.Count == 0)
        {
            return null;
        }

        var attribute = context.Attributes.FirstOrDefault(a => a.ConstructorArguments.Length > 0);
        var flowId = attribute?.ConstructorArguments[0].Value as string ?? symbol.Name;

        return new FlowTriggersModel(
            flowId,
            declared,
            TriggerReader.ReadSchedules(symbol),
            TriggerReader.ReadStreams(symbol));
    }

    private static void ProduceManifest(
        SourceProductionContext production,
        ImmutableArray<AnalysisResult?> results,
        ImmutableArray<FlowTriggersModel?> triggers,
        ImmutableArray<CapabilityErrorCatalogue?> errorCatalogues,
        string applicationName,
        string? projectDirectory)
    {
        var models = results
            .Where(static r => r is { IsSuccess: true, Model: not null })
            .Select(static r => r!.Model!)
            .ToList();

        if (models.Count == 0)
        {
            // No flows, or none that analysed cleanly. Emitting an empty manifest here
            // would let a build with errors publish a document claiming the application
            // has no flows, which is a more dangerous lie than emitting nothing.
            return;
        }

        var manifest = ManifestWriter.Write(
            applicationName,
            "1.0.0",
            models,
            projectDirectory,
            [.. triggers.Where(static t => t is not null).Select(static t => t!)],
            [.. errorCatalogues.Where(static c => c is not null).Select(static c => c!)]);

        production.AddSource("FlowXManifest.g.cs", SourceText.From(EmitManifestHolder(manifest), Encoding.UTF8));
    }

    /// <summary>
    /// Wraps the manifest JSON in a C# constant.
    /// </summary>
    /// <remarks>
    /// A source generator must not write files. It runs inside the IDE on every
    /// keystroke, its output is cached by the compiler, and file IO from that position
    /// breaks incrementality and races with the build. So the manifest travels as a
    /// compiled-in constant, and <c>flowx manifest</c> (WP-9) writes it to disk from
    /// there. The build artifact ADR-0005 asks for is produced by the CLI; the content
    /// is produced here, deterministically.
    /// </remarks>
    private static string EmitManifestHolder(string manifest)
    {
        var writer = new SourceWriter();

        writer.Line(FlowEmitter.Header.TrimEnd('\n'));
        writer.Line("#nullable enable");
        writer.Line();
        writer.Line("namespace FlowX.Generated;");
        writer.Line();
        writer.Line("/// <summary>The application's compiled manifest. See ADR-0005.</summary>");
        writer.Line("public static class FlowXManifest");
        writer.OpenBrace();
        writer.Line("/// <summary>The manifest document, byte-identical across builds of identical source.</summary>");
        writer.Line("public const string Json = @\"" + manifest.Replace("\"", "\"\"") + "\";");
        writer.CloseBrace();

        return writer.ToString();
    }

    private static AnalysisResult? Analyze(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context.TargetNode is not ClassDeclarationSyntax declaration ||
            context.TargetSymbol is not INamedTypeSymbol symbol)
        {
            return null;
        }

        return FlowAnalyzer.Analyze(symbol, declaration, context.SemanticModel);
    }

    /// <summary>
    /// Emits one flow's plan and dispatcher, and settles that flow's <c>FLOWX1024</c>s.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why the warning is decided here and not in analysis.</strong> Whether an
    /// <c>.Emit</c> step stages anything turns on two facts. The flow's profile is one, and
    /// <c>FlowAnalyzer</c> knows it. The other — whether exactly one
    /// <c>JsonSerializerContext</c> in this compilation declares the event contract — is a
    /// question about every tree in the build, and a per-flow transform that walked them all
    /// to answer it would trade the generator's incrementality for a warning. So analysis
    /// raises a provisional diagnostic carrying the contract in its properties, and this
    /// drops it or restates it with the reason that applies.
    /// </para>
    /// <para>
    /// Restated rather than mutated, because a <c>Diagnostic</c>'s message arguments are
    /// fixed at creation. The location is the provisional one's, so a
    /// <c>#pragma warning disable</c> around the author's <c>.Emit</c> still suppresses it.
    /// </para>
    /// </remarks>
    private static void Produce(
        SourceProductionContext production,
        AnalysisResult result,
        ImmutableArray<JsonContextModel?> jsonContexts)
    {
        var contexts = jsonContexts
            .Where(static c => c is not null)
            .Select(static c => c!)
            .ToList();

        // Journaled, not "Durable" — and the variable is named for the question rather than for
        // one of its answers, because the old name is how the bug survived reading. A Streaming
        // flow stages an event for the same reason a durable one does; while this compared the
        // profile name literally it restated every FLOWX1024 on a windowing flow as "the flow
        // declares Profile = Ephemeral", a sentence that was false about a flow declaring
        // Streaming.
        var journaled = ExecutionProfiles.Journals(result.Model?.Profile);

        foreach (var diagnostic in result.Diagnostics)
        {
            if (string.Equals(diagnostic.Id, EmitDiagnosticId, StringComparison.Ordinal))
            {
                Report(production, SettleEmitDiagnostic(diagnostic, contexts, journaled));
                continue;
            }

            if (string.Equals(diagnostic.Id, StateDiagnosticId, StringComparison.Ordinal))
            {
                Report(production, SettleStateDiagnostic(diagnostic, contexts));
                continue;
            }

            production.ReportDiagnostic(diagnostic);
        }

        if (!result.IsSuccess || result.Model is null)
        {
            // Diagnostics have been reported; emitting a partial plan on top of them
            // would bury the real error under a cascade of "type not found".
            return;
        }

        production.AddSource(
            FlowEmitter.FileNameFor(result.Model),
            SourceText.From(FlowEmitter.Emit(result.Model, contexts), Encoding.UTF8));
    }

    /// <summary>
    /// Turns one provisional <c>FLOWX1024</c> into the diagnostic to report, or into nothing.
    /// </summary>
    /// <returns>
    /// <c>null</c> when the event is staged and published, so there is nothing to warn about.
    /// </returns>
    private static Diagnostic? SettleEmitDiagnostic(
        Diagnostic provisional, List<JsonContextModel> contexts, bool journaled)
    {
        provisional.Properties.TryGetValue(EmitReasons.ContractProperty, out var contract);
        provisional.Properties.TryGetValue(EmitReasons.NameProperty, out var name);

        if (!journaled)
        {
            return Restate(provisional, name, EmitReasons.Ephemeral);
        }

        if (contract is null)
        {
            // The type argument did not resolve, so analysis produced no step either. C# is
            // already reporting something more useful about the same span.
            return null;
        }

        return FlowEmitter.SingleContextFor(contexts, contract) is null
            ? Restate(
                provisional,
                name,
                string.Format(CultureInfo.InvariantCulture, EmitReasons.NoSerializerContextFormat, contract))
            : null;
    }

    private static Diagnostic Restate(Diagnostic provisional, string? name, string reason) =>
        Diagnostic.Create(
            FlowXDiagnostics.EmitIsNotYetPublished,
            provisional.Location,
            provisional.Properties,
            name ?? "TEvent",
            reason);

    /// <summary>
    /// Turns one provisional <c>FLOWX1006</c> into the diagnostic to report, or into nothing.
    /// </summary>
    /// <returns>
    /// <c>null</c> when exactly one serialiser context declares the contract, so the generated
    /// payload writer can name metadata for it and there is nothing to report.
    /// </returns>
    /// <remarks>
    /// The profile is not re-read here, unlike <c>FLOWX1024</c>'s settlement: analysis raises
    /// this provisional only for a <c>Durable</c> flow, because an ephemeral one keeps no
    /// journal and the rule protects nothing there.
    /// </remarks>
    private static Diagnostic? SettleStateDiagnostic(
        Diagnostic provisional, List<JsonContextModel> contexts)
    {
        provisional.Properties.TryGetValue(StateBagReasons.ContractProperty, out var contract);
        provisional.Properties.TryGetValue(StateBagReasons.NameProperty, out var name);
        provisional.Properties.TryGetValue(StateBagReasons.FlowProperty, out var flowId);

        if (contract is null)
        {
            // The contract did not resolve, so analysis produced no usable name either. C# is
            // already reporting something more useful about the same span.
            return null;
        }

        return FlowEmitter.SingleContextFor(contexts, contract) is null
            ? Diagnostic.Create(
                FlowXDiagnostics.StateIsNotSerialisable,
                provisional.Location,
                provisional.Properties,
                name ?? contract,
                flowId ?? string.Empty,
                string.Format(
                    CultureInfo.InvariantCulture,
                    StateBagReasons.NoSerializerContextFormat,
                    contract))
            : null;
    }

    /// <summary>Reports a settled diagnostic, or nothing when it settled to nothing.</summary>
    private static void Report(SourceProductionContext production, Diagnostic? settled)
    {
        if (settled is not null)
        {
            production.ReportDiagnostic(settled);
        }
    }
}
