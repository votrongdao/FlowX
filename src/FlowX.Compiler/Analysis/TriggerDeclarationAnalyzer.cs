using System.Collections.Immutable;
using System.Linq;
using FlowX.Compiler.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FlowX.Compiler.Analysis;

/// <summary>
/// Reports a trigger attribute that declares no <c>[TriggerKind]</c>: FLOWX1025.
/// </summary>
/// <remarks>
/// <para>
/// A trigger's <c>Kind</c> property is abstract and each attribute overrides it —
/// executable code, not attribute data — so it cannot be read from metadata. The kind is
/// therefore declared a second time <em>as</em> data, with <c>[TriggerKind(...)]</c> on the
/// attribute class, and <see cref="TriggerReader.KindOf"/> reads that, across an assembly
/// boundary, without running anything. An attribute carrying no marker declares no family
/// the compiler can read and is skipped rather than guessed at, which
/// <a href="../../../docs/adr/ADR-0005-manifest-as-build-artifact.md">ADR-0005</a>
/// requires: the manifest publishes what was declared, never what was plausible.
/// </para>
/// <para>
/// <strong>What the rule says now.</strong> Not "this trigger cannot be read" — since the
/// marker exists, it can be — but "this trigger attribute declares no kind", which is a
/// defect with an owner and a one-line fix. The consequence is unchanged, and is why it is
/// worth reporting: the build succeeds, the manifest carries no <c>triggers</c> entry, and
/// <c>flowx diff</c> — which classifies a removed trigger as breaking — sees an absence
/// indistinguishable from a flow that declares no trigger at all. A contract gate that
/// silently loses its input is worse than no gate, because a green result is read as a
/// checked result.
/// </para>
/// <para>
/// A <see cref="DiagnosticAnalyzer"/> rather than a generator diagnostic, matching
/// <see cref="CapabilityAnalyzer"/> and <see cref="StepBindingAnalyzer"/>. The generator's
/// trigger pipeline drops an undeclared attribute before it has anywhere to report from,
/// and the question is worth answering in the editor on the keystroke that applies the
/// attribute, not only when the generator next runs.
/// </para>
/// <para>
/// <strong>Scoped to <c>[Flow]</c> types.</strong> A trigger attribute applied to
/// anything else reaches no manifest either way, so reporting there would be noise about
/// a document the type was never going to appear in.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TriggerDeclarationAnalyzer : DiagnosticAnalyzer
{
    private const string FlowAttributeName = "FlowX.FlowAttribute";

    /// <summary>The attribute a schedule is declared with, and the input a schedule can give.</summary>
    private const string CronTriggerAttributeName = "FlowX.CronTriggerAttribute";

    private const string ScheduledFireName = "FlowX.ScheduledFire";

    /// <summary>The attributes a subscription is declared with, and the input one can give.</summary>
    /// <remarks>
    /// Matched by name rather than by <c>[TriggerKind(TriggerKind.Bus)]</c>, and the difference
    /// matters. A third-party bus attribute reaching the manifest with its kind produces no
    /// registration either — <c>TriggerReader</c> has no shape for its arguments, so there is no
    /// topic to read — and reporting FLOWX1039 on it would tell an author their flow is wrong
    /// when what is missing is a shape this build does not know. FLOWX1025 is the rule that
    /// covers a plugin attribute, and it says something an author can act on.
    /// </remarks>
    private static readonly string[] BusTriggerAttributeNames =
    [
        "FlowX.BusTriggerAttribute",
        "FlowX.KafkaTriggerAttribute",
        "FlowX.RabbitMqTriggerAttribute",
        "FlowX.ServiceBusTriggerAttribute",
    ];

    /// <summary>The attribute a change subscription is declared with.</summary>
    /// <remarks>
    /// Matched by name rather than by <c>[TriggerKind(TriggerKind.Change)]</c>, for
    /// <see cref="BusTriggerAttributeNames"/>'s reason: a third-party <c>Change</c> attribute
    /// reaching the manifest with its kind produces no registration either, because
    /// <c>TriggerReader</c> has no shape for its arguments, and FLOWX1025 is the rule that covers
    /// it with something the author can act on.
    /// </remarks>
    private const string ChangeTriggerAttributeName = "FlowX.ChangeTriggerAttribute";

    /// <summary>The attribute a stream subscription is declared with.</summary>
    /// <remarks>
    /// Matched by name for <see cref="BusTriggerAttributeNames"/>'s reason.
    /// </remarks>
    private const string StreamTriggerAttributeName = "FlowX.StreamTriggerAttribute";

    private const string StreamWindowBatchName = "FlowX.StreamWindowBatch";

    /// <summary>The prefix of the only window shape the stream engine implements.</summary>
    /// <remarks>
    /// Repeated here rather than read from <c>StreamWindowSpec.TumblingPrefix</c>, because this
    /// assembly is netstandard2.0 and references no runtime assembly. The two are kept in step by
    /// a pair of tests over the same shapes:
    /// <c>StreamGenerationTests.AWindowShapeTheEngineDoesNotImplementIsReported</c> here and
    /// <c>StreamWindowAssignerTests.AWindowShapeTheEngineDoesNotImplementIsRefused</c> there.
    /// </remarks>
    private const string TumblingPrefix = "tumbling:";

    private const string BusMessageName = "FlowX.BusMessage";
    private const string TriggerDecoderName = "FlowX.ITriggerDecoder";

    private const string FlowBaseName = "FlowX.Flow";

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(
            FlowXDiagnostics.TriggerDeclaresNoKind,
            FlowXDiagnostics.ScheduledFlowCannotBeFired,
            FlowXDiagnostics.BusFlowCannotBeConsumed,
            FlowXDiagnostics.ChangeFlowCannotBeObserved,
            FlowXDiagnostics.StreamFlowCannotBeWindowed,
            FlowXDiagnostics.ScheduleJitterCannotBeRead,
            FlowXDiagnostics.StreamWindowArgumentCannotBeRead,
            FlowXDiagnostics.TriggerInputContractsConflict,
            FlowXDiagnostics.TriggerDecoderDoesNotProduceTheInput);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            return;
        }

        // The generated plan is a second part of the flow's class and carries no
        // attributes of its own; analysing it would only re-report the hand-written
        // declaration against a file nobody can edit.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSymbolAction(Analyze, SymbolKind.NamedType);
    }

    private static void Analyze(SymbolAnalysisContext context)
    {
        if (context.Symbol is not INamedTypeSymbol type)
        {
            return;
        }

        var attributes = type.GetAttributes();

        if (!attributes.Any(static a => a.AttributeClass?.ToDisplayString() == FlowAttributeName))
        {
            return;
        }

        foreach (var attribute in attributes)
        {
            // Exactly what TriggerReader skips: a trigger whose family it cannot read.
            if (!TriggerReader.IsTrigger(attribute.AttributeClass) ||
                TriggerReader.KindOf(attribute.AttributeClass) is not null)
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                FlowXDiagnostics.TriggerDeclaresNoKind,
                LocationOf(attribute, type, context.CancellationToken),
                SeverityFor(attribute.AttributeClass!),
                additionalLocations: null,
                properties: null,
                attribute.AttributeClass!.Name,
                type.Name));
        }

        // Reported first and instead of everything below, for the same reason the conflict rule
        // is: an author who named a decoder has already answered the four contract rules'
        // question, and being told to declare the payload as the input would send them to undo
        // the thing they were doing.
        if (ReportUndecodableTriggers(context, type, attributes))
        {
            return;
        }

        // Reported instead of the four below and never beside them. Each of those tells the
        // author to declare the input contract its own transport needs, and on a flow carrying
        // two of them following either message re-raises the other — so the actionable-looking
        // message has to be the one that names the real fact.
        if (ReportConflictingTriggerInputs(context, type, attributes))
        {
            return;
        }

        ReportUnfireableSchedules(context, type, attributes);
        ReportUnreadableJitter(context, type, attributes);
        ReportUnconsumableSubscriptions(context, type, attributes);
        ReportUnobservableChangeSubscriptions(context, type, attributes);
        ReportUnwindowableStreams(context, type, attributes);
        ReportUnreadableWindowArguments(context, type, attributes);
    }

    /// <summary>
    /// Reports FLOWX1048 when this flow's triggers cannot share one input contract.
    /// </summary>
    /// <returns><c>true</c> when it reported, which suppresses the four rules it replaces.</returns>
    /// <remarks>
    /// <para>
    /// <strong>Judged on the trigger kinds and never on the flow's declared input.</strong> Two
    /// triggers demanding different contracts are in conflict whichever of the two the class
    /// happens to declare — and whether it declares neither. Reading the input as well would make
    /// the rule silent on exactly the flow whose author has not chosen yet.
    /// </para>
    /// <para>
    /// <strong><c>[HttpTrigger]</c> and <c>[AgentTrigger]</c> are not in the set, and that is not
    /// an omission.</strong> They bind the flow's own request contract, whatever it is, so they
    /// conflict with nothing: a <c>Flow&lt;BusMessage, TOut&gt;</c> reachable over HTTP is
    /// unusual and is a decision its author can defend, not a build error.
    /// </para>
    /// </remarks>
    private static bool ReportConflictingTriggerInputs(
        SymbolAnalysisContext context, INamedTypeSymbol type, ImmutableArray<AttributeData> attributes)
    {
        var demands = attributes
            .Select(static a => ContractStillDemandedBy(a))
            .Where(static demand => demand is not null)
            .Select(static demand => demand!)
            .Distinct()
            .OrderBy(static demand => demand, System.StringComparer.Ordinal)
            .ToArray();

        if (demands.Length < 2)
        {
            return false;
        }

        var reason = string.Join(", ", attributes
            .Where(static a => ContractStillDemandedBy(a) is not null)
            .Select(static a =>
                $"[{a.AttributeClass!.Name.Replace("Attribute", string.Empty)}] needs " +
                ContractStillDemandedBy(a))
            .Distinct()
            .OrderBy(static text => text, System.StringComparer.Ordinal));

        foreach (var attribute in attributes)
        {
            if (ContractStillDemandedBy(attribute) is null)
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                FlowXDiagnostics.TriggerInputContractsConflict,
                LocationOf(attribute, type, context.CancellationToken),
                FlowIdOf(type, attributes),
                reason));
        }

        return true;
    }

    /// <summary>
    /// The input contract a trigger attribute forces on the flow, or <c>null</c> when it forces
    /// none.
    /// </summary>
    /// <remarks>
    /// The same by-name matching the four rules below use, for
    /// <see cref="BusTriggerAttributeNames"/>'s reason: a third-party attribute carrying a kind
    /// but no shape this build knows produces no registration either, and FLOWX1025 is the rule
    /// that covers it with something the author can act on.
    /// </remarks>
    private static string? ContractDemandedBy(string? attributeName) => attributeName switch
    {
        CronTriggerAttributeName => ScheduledFireName,
        ChangeTriggerAttributeName => BusMessageName,
        StreamTriggerAttributeName => StreamWindowBatchName,
        _ => System.Array.IndexOf(BusTriggerAttributeNames, attributeName) >= 0 ? BusMessageName : null,
    };

    /// <summary>
    /// The contract a trigger still forces on the flow after its <c>Decode</c> is taken into
    /// account, or <c>null</c> when it forces none.
    /// </summary>
    /// <remarks>
    /// A named decoder <em>is</em> the answer to "what starts this flow", so a trigger carrying
    /// one demands nothing of the class — which is what lets a bus trigger and a schedule sit on
    /// one flow. Whether the decoder actually produces the right thing is
    /// <see cref="ReportUndecodableTriggers"/>'s question, and it is asked separately so that a
    /// wrong decoder is reported as a wrong decoder rather than as a conflict the author did not
    /// write.
    /// </remarks>
    private static string? ContractStillDemandedBy(AttributeData attribute) =>
        DecoderNamedBy(attribute) is not null
            ? null
            : ContractDemandedBy(attribute.AttributeClass?.ToDisplayString());

    /// <summary>The type a trigger's <c>Decode</c> names, or <c>null</c> when it names none.</summary>
    private static INamedTypeSymbol? DecoderNamedBy(AttributeData attribute) =>
        attribute.NamedArguments
            .FirstOrDefault(pair => pair.Key == "Decode")
            .Value.Value as INamedTypeSymbol;

    /// <summary>
    /// Whether a type decodes this trigger's payload into this flow's input.
    /// </summary>
    /// <remarks>
    /// Asked of the interface's type arguments rather than of the method, because a type may
    /// implement the interface several times and only one of those implementations is the one
    /// this declaration is claiming.
    /// </remarks>
    private static bool Decodes(INamedTypeSymbol decoder, string payload, ITypeSymbol input) =>
        decoder.AllInterfaces.Any(contract =>
            contract.ConstructedFrom.ToDisplayString().StartsWith(TriggerDecoderName, System.StringComparison.Ordinal)
            && contract.TypeArguments.Length == 2
            && contract.TypeArguments[0].ToDisplayString() == payload
            && SymbolEqualityComparer.Default.Equals(contract.TypeArguments[1], input));

    /// <summary>
    /// Reports FLOWX1051 on each trigger whose <c>Decode</c> cannot produce the flow's input.
    /// </summary>
    /// <returns><c>true</c> when it reported, which suppresses the rules a decoder stands in for.</returns>
    /// <remarks>
    /// Reported before the four contract rules and instead of them, for
    /// <see cref="ReportConflictingTriggerInputs"/>'s reason: an author who has named a decoder
    /// has already answered those rules' question, and being told to declare the payload as the
    /// input would send them to undo the thing they were trying to do.
    /// </remarks>
    private static bool ReportUndecodableTriggers(
        SymbolAnalysisContext context, INamedTypeSymbol type, ImmutableArray<AttributeData> attributes)
    {
        if (InputOf(type) is not { } input)
        {
            return false;
        }

        var reported = false;

        foreach (var attribute in attributes)
        {
            if (DecoderNamedBy(attribute) is not { } decoder
                || ContractDemandedBy(attribute.AttributeClass?.ToDisplayString()) is not { } payload
                || Decodes(decoder, payload, input))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                FlowXDiagnostics.TriggerDecoderDoesNotProduceTheInput,
                LocationOf(attribute, type, context.CancellationToken),
                FlowIdOf(type, attributes),
                decoder.Name,
                payload,
                input.ToDisplayString()));

            reported = true;
        }

        return reported;
    }

    /// <summary>
    /// Reports FLOWX1045 on each <c>[CronTrigger]</c> whose <c>Jitter</c> the host would refuse.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Per attribute rather than per flow</strong>, unlike
    /// <see cref="ReportUnfireableSchedules"/>: the spread is a property of one declaration, so a
    /// flow with two schedules gets a report on the one whose window is unreadable and silence on
    /// the other. That is <see cref="ReportUnwindowableStreams"/>'s arrangement, and it is here
    /// for the same reason.
    /// </para>
    /// <para>
    /// Reported whether or not the flow is fireable at all. The two are independent defects with
    /// independent fixes, and suppressing one because the other is also present would mean an
    /// author repairs the profile, rebuilds, and discovers a second error they could have seen
    /// the first time.
    /// </para>
    /// </remarks>
    private static void ReportUnreadableJitter(
        SymbolAnalysisContext context, INamedTypeSymbol type, ImmutableArray<AttributeData> attributes)
    {
        foreach (var attribute in attributes)
        {
            if (attribute.AttributeClass?.ToDisplayString() != CronTriggerAttributeName)
            {
                continue;
            }

            var jitter = attribute.NamedArguments
                .Where(static pair => pair.Key == "Jitter")
                .Select(static pair => pair.Value.Value as string)
                .FirstOrDefault();

            if (jitter is null || UnreadableJitterReason(jitter) is not { } reason)
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                FlowXDiagnostics.ScheduleJitterCannotBeRead,
                LocationOf(attribute, type, context.CancellationToken),
                FlowIdOf(type, attributes),
                jitter,
                reason));
        }
    }

    /// <summary>
    /// Why this spread is not one the host can register, or <c>null</c> when it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Parsed here rather than by calling the runtime's reader, which this assembly
    /// cannot reference.</strong> <see cref="System.Xml.XmlConvert.ToTimeSpan"/> is what
    /// <c>ScheduleJitter.Read</c> calls and it is available on netstandard2.0, so the two are the
    /// same function rather than two parsers to keep in step —
    /// <c>DeadlineCoherenceAnalyzer.Iso8601Duration</c> takes the same route for the same reason.
    /// </para>
    /// <para>
    /// The one thing not judged is whether the spread is wider than the gap between two
    /// occurrences. That is a property of the expression as well as the window, this rule has the
    /// expression as a string and no cron evaluator, and the answer is a design question rather
    /// than a defect: an author who spreads an hourly schedule over ninety minutes has asked for
    /// firings that overtake each other, and <c>OverlapPolicy</c> is what decides what happens
    /// then.
    /// </para>
    /// </remarks>
    private static string? UnreadableJitterReason(string jitter)
    {
        if (jitter.Trim().Length == 0)
        {
            return "it is empty. Omit the property for a schedule that fires on its occurrence";
        }

        System.TimeSpan window;

        try
        {
            window = System.Xml.XmlConvert.ToTimeSpan(jitter);
        }
        catch (System.FormatException)
        {
            return "it is not an ISO-8601 duration — write PT30S, PT2M or PT1H";
        }
        catch (System.OverflowException)
        {
            return "it does not fit in a TimeSpan";
        }

        return window > System.TimeSpan.Zero
            ? null
            : "a spread has to be positive, and this one is not — omit the property rather than " +
              "declaring a window nothing is spread over";
    }

    /// <summary>
    /// Reports FLOWX1042 on each stream trigger the host would have nothing to do with.
    /// </summary>
    /// <remarks>
    /// <see cref="ReportUnobservableChangeSubscriptions"/>'s shape, with one difference that is
    /// the reason this is a separate rule rather than a widened one: two of the three reasons are
    /// properties of the flow and the third is a property of <em>this attribute</em>. So the
    /// window is read per attribute rather than once per flow, and a flow declaring two streams
    /// gets a report on the one whose window this engine cannot serve and silence on the other.
    /// </remarks>
    private static void ReportUnwindowableStreams(
        SymbolAnalysisContext context, INamedTypeSymbol type, ImmutableArray<AttributeData> attributes)
    {
        if (!attributes.Any(static a =>
                a.AttributeClass?.ToDisplayString() == StreamTriggerAttributeName))
        {
            return;
        }

        var flowReason = UnwindowableFlowReason(type, attributes);

        foreach (var attribute in attributes)
        {
            if (attribute.AttributeClass?.ToDisplayString() != StreamTriggerAttributeName)
            {
                continue;
            }

            var reason = flowReason ?? UnservableWindowReason(attribute);

            if (reason is null)
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                FlowXDiagnostics.StreamFlowCannotBeWindowed,
                LocationOf(attribute, type, context.CancellationToken),
                FlowIdOf(type, attributes),
                reason));
        }
    }

    /// <summary>
    /// Why no window could start this flow, or <c>null</c> when one could.
    /// </summary>
    /// <remarks>
    /// <see cref="UnobservableReason"/>'s two conditions with both terms changed, checked in the
    /// order an author would repair them.
    /// </remarks>
    private static string? UnwindowableFlowReason(
        INamedTypeSymbol type, ImmutableArray<AttributeData> attributes)
    {
        if (InputOf(type) is not { } input)
        {
            // The base type did not resolve, so C# is already reporting something more useful
            // about the same span and this rule would be piling on.
            return null;
        }

        if (input.ToDisplayString() != StreamWindowBatchName)
        {
            return
                $"its input contract is '{input.ToDisplayString()}' and a closed window has an " +
                "interval and its records to give it — declare it as " +
                "Flow<StreamWindowBatch, TOut> and deserialise each record's payload in a " +
                "capability";
        }

        return IsStreaming(attributes)
            ? null
            : "it does not declare ExecutionProfile.Streaming, so nothing journals its " +
              "instances, the instance id a window derives is inert, and — because the " +
              "checkpoint is committed after the window's flow has run — every crash in between " +
              "would aggregate that window a second time — declare " +
              "Profile = ExecutionProfile.Streaming";
    }

    /// <summary>
    /// Why this declaration's window is not one the engine serves, or <c>null</c> when it is.
    /// </summary>
    /// <remarks>
    /// Only the shape family is judged, not the duration inside it: this assembly cannot call the
    /// runtime's reader, and a rule that reimplemented duration parsing would be a second parser
    /// to keep in step for the sake of an error <c>FlowStreamCatalog.Add</c> already gives at
    /// startup with better words.
    /// </remarks>
    private static string? UnservableWindowReason(AttributeData attribute)
    {
        var window = attribute.NamedArguments
            .Where(static pair => pair.Key == "Window")
            .Select(static pair => pair.Value.Value as string)
            .FirstOrDefault();

        if (window is null)
        {
            // Window is `required`, so C# has already reported its absence.
            return null;
        }

        return window.StartsWith(TumblingPrefix, System.StringComparison.Ordinal)
            ? null
            : $"its window is '{window}' and this engine implements tumbling windows only — a " +
              "sliding or session window assigns a record to a window whose bounds are not a " +
              "function of the event time alone, so a window rebuilt after a crash would not " +
              "derive the id that deduplicates it, and a global window is never closed by a " +
              "watermark so nothing would ever run — declare Window = \"tumbling:<duration>\"";
    }

    /// <summary>
    /// Reports FLOWX1049 on each <c>[StreamTrigger]</c> argument <c>StreamWindowSpec.Read</c>
    /// would refuse and <see cref="ReportUnwindowableStreams"/> does not judge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ReportUnreadableJitter"/>'s arrangement, one transport over: per attribute
    /// rather than per flow, and reported whether or not the flow is windowable at all, because
    /// the two are independent defects with independent fixes.
    /// </para>
    /// <para>
    /// <strong>One report per attribute, naming the first argument it would be refused
    /// for.</strong> <c>Read</c> stops at the first failure too, so a declaration with two bad
    /// values gets the same message from this rule and from the host — and an author repairing
    /// one and rebuilding is told about the other, which is the order the values are validated
    /// in rather than a rule that hides one behind another.
    /// </para>
    /// </remarks>
    private static void ReportUnreadableWindowArguments(
        SymbolAnalysisContext context, INamedTypeSymbol type, ImmutableArray<AttributeData> attributes)
    {
        foreach (var attribute in attributes)
        {
            if (attribute.AttributeClass?.ToDisplayString() != StreamTriggerAttributeName)
            {
                continue;
            }

            if (UnreadableWindowArgument(attribute) is not { } refusal)
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                FlowXDiagnostics.StreamWindowArgumentCannotBeRead,
                LocationOf(attribute, type, context.CancellationToken),
                FlowIdOf(type, attributes),
                refusal.Declaration,
                refusal.Reason));
        }
    }

    /// <summary>What this declaration writes, and why the host would refuse it.</summary>
    private readonly struct Refusal(string declaration, string reason)
    {
        /// <summary>The property and the value, as the author wrote them.</summary>
        public string Declaration { get; } = declaration;

        /// <summary>Why <c>StreamWindowSpec.Read</c> would not take it.</summary>
        public string Reason { get; } = reason;
    }

    /// <summary>
    /// The first of <c>Lateness</c>, <c>Checkpoint</c> and <c>Parallelism</c> the host would
    /// refuse, or <c>null</c> when it would take all three.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Checked in <c>StreamWindowSpec.Read</c>'s own order</strong>, so the value this
    /// names is the value the host would name.
    /// </para>
    /// <para>
    /// <strong>Parsed here rather than by calling the runtime's reader, which this assembly
    /// cannot reference.</strong> <see cref="System.Xml.XmlConvert.ToTimeSpan"/> is what
    /// <c>StreamWindowSpec.Read</c> calls and it is available on netstandard2.0, so the two are
    /// the same function rather than two parsers to keep in step —
    /// <see cref="UnreadableJitterReason"/> takes the same route for the same reason. The
    /// <em>window</em> is the one argument that cannot be checked this way, because its short
    /// form has no framework parser; <c>FLOWX1042</c> judges only its shape family for that
    /// reason.
    /// </para>
    /// </remarks>
    private static Refusal? UnreadableWindowArgument(AttributeData attribute)
    {
        if (Argument(attribute, "Lateness") is string lateness &&
            UnreadableDurationReason(lateness) is { } latenessReason)
        {
            return new Refusal($"Lateness = \"{lateness}\"", latenessReason);
        }

        if (Argument(attribute, "Checkpoint") is string checkpoint &&
            UnreadableDurationReason(checkpoint) is { } checkpointReason)
        {
            return new Refusal($"Checkpoint = \"{checkpoint}\"", checkpointReason);
        }

        return Argument(attribute, "Parallelism") is int parallelism && parallelism < 1
            ? new Refusal(
                $"Parallelism = {parallelism.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                "it must be at least one. Zero is not 'the engine decides' — it is a " +
                "subscription that reads a stream and never runs a flow")
            : null;
    }

    /// <summary>Why this duration is not one the host can read, or <c>null</c> when it is.</summary>
    /// <remarks>
    /// <strong>Zero is accepted, unlike <see cref="UnreadableJitterReason"/>'s.</strong> A jitter
    /// of nothing asks for a spread and gets none; a lateness of nothing is the ordinary
    /// declaration for a stream already in order, and a checkpoint of nothing means commit after
    /// every window. Only a negative value is refused, which is <c>StreamWindowSpec.Read</c>'s
    /// own boundary rather than a second opinion about it.
    /// </remarks>
    private static string? UnreadableDurationReason(string value)
    {
        if (value.Trim().Length == 0)
        {
            return "it is empty. Omit the property to take the attribute's default";
        }

        System.TimeSpan duration;

        try
        {
            duration = System.Xml.XmlConvert.ToTimeSpan(value);
        }
        catch (System.FormatException)
        {
            return "it is not an ISO-8601 duration — write PT10S, PT5S or PT1M. The short form " +
                   "Window takes, '1m', is not a second spelling this property accepts";
        }
        catch (System.OverflowException)
        {
            return "it does not fit in a TimeSpan";
        }

        return duration < System.TimeSpan.Zero
            ? "it is negative, and neither a lateness allowance nor a commit interval can run " +
              "backwards"
            : null;
    }

    /// <summary>One named argument's value, or <c>null</c> when the declaration omits it.</summary>
    private static object? Argument(AttributeData attribute, string name) => attribute.NamedArguments
        .Where(pair => pair.Key == name)
        .Select(static pair => pair.Value.Value)
        .FirstOrDefault();

    /// <summary>Whether the flow declares <c>Profile = ExecutionProfile.Streaming</c>.</summary>
    private static bool IsStreaming(ImmutableArray<AttributeData> attributes) => attributes
        .Where(static a => a.AttributeClass?.ToDisplayString() == FlowAttributeName)
        .SelectMany(static a => a.NamedArguments)
        .Any(static pair => pair.Key == "Profile" && pair.Value.Value is int profile && profile == 2);

    /// <summary>
    /// Reports FLOWX1041 on each change trigger the host would have nothing to do with.
    /// </summary>
    /// <remarks>
    /// <see cref="ReportUnconsumableSubscriptions"/>'s shape and reasons. The conditions are the
    /// same two and the <em>consequence</em> of the second is not — a re-read rather than a
    /// redelivery — which is why the reason text is written out here rather than shared.
    /// </remarks>
    private static void ReportUnobservableChangeSubscriptions(
        SymbolAnalysisContext context, INamedTypeSymbol type, ImmutableArray<AttributeData> attributes)
    {
        var reason = UnobservableReason(type, attributes);

        if (reason is null)
        {
            return;
        }

        foreach (var attribute in attributes)
        {
            if (attribute.AttributeClass?.ToDisplayString() != ChangeTriggerAttributeName)
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                FlowXDiagnostics.ChangeFlowCannotBeObserved,
                LocationOf(attribute, type, context.CancellationToken),
                FlowIdOf(type, attributes),
                reason));
        }
    }

    /// <summary>
    /// Why nothing could observe this flow's change subscriptions, or <c>null</c> when something
    /// can.
    /// </summary>
    /// <remarks>
    /// The order <see cref="UnconsumableReason"/> checks in, and for its reason: a flow whose
    /// input is wrong cannot be started at all, and a flow whose profile is wrong would be
    /// started again on every re-read.
    /// </remarks>
    private static string? UnobservableReason(
        INamedTypeSymbol type, ImmutableArray<AttributeData> attributes)
    {
        if (!attributes.Any(static a =>
                a.AttributeClass?.ToDisplayString() == ChangeTriggerAttributeName))
        {
            return null;
        }

        if (InputOf(type) is not { } input)
        {
            // The base type did not resolve, so C# is already reporting something more useful
            // about the same span and this rule would be piling on.
            return null;
        }

        if (input.ToDisplayString() != BusMessageName)
        {
            return
                $"its input contract is '{input.ToDisplayString()}' and a change is an outbox row " +
                "with only the message to give it — declare it as Flow<BusMessage, TOut> and " +
                "deserialise the payload in a capability";
        }

        return IsDurable(attributes)
            ? null
            : "it does not declare ExecutionProfile.Durable, so nothing journals its instances, " +
              "the instance id a change derives is inert, and — because the cursor is committed " +
              "after the flows have run — every crash in between would start the flow again — " +
              "declare Profile = ExecutionProfile.Durable";
    }

    /// <summary>
    /// Reports FLOWX1039 on each bus trigger the host would have nothing to do with.
    /// </summary>
    /// <remarks>
    /// <see cref="ReportUnfireableSchedules"/>'s shape and its reasons: the analyzer has the
    /// attribute's own span where the generator has a collected model and nothing to point at,
    /// and one report per attribute rather than one per flow makes "remove this or fix the flow"
    /// a decision the author can take per line.
    /// </remarks>
    private static void ReportUnconsumableSubscriptions(
        SymbolAnalysisContext context, INamedTypeSymbol type, ImmutableArray<AttributeData> attributes)
    {
        var reason = UnconsumableReason(type, attributes);

        if (reason is null)
        {
            return;
        }

        foreach (var attribute in attributes)
        {
            if (!IsBusTrigger(attribute))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                FlowXDiagnostics.BusFlowCannotBeConsumed,
                LocationOf(attribute, type, context.CancellationToken),
                FlowIdOf(type, attributes),
                reason));
        }
    }

    /// <summary>
    /// Why nothing could consume this flow's subscriptions, or <c>null</c> when something can.
    /// </summary>
    /// <remarks>
    /// The two reasons are checked in the order an author would repair them, for
    /// <see cref="UnfireableReason"/>'s reason: a flow whose input is wrong cannot be started at
    /// all, and a flow whose profile is wrong would be started once per delivery.
    /// </remarks>
    private static string? UnconsumableReason(
        INamedTypeSymbol type, ImmutableArray<AttributeData> attributes)
    {
        // A subscription that names a decoder has answered this rule's question already: the
        // delivery becomes the flow's input through the named translation, so requiring the
        // class to declare the payload would be requiring it to undo that. Only the
        // undecoded ones are judged here, and a decoder that cannot do the job is FLOWX1051.
        if (!attributes.Any(a => IsBusTrigger(a) && DecoderNamedBy(a) is null))
        {
            return null;
        }

        if (InputOf(type) is not { } input)
        {
            // The base type did not resolve, so C# is already reporting something more useful
            // about the same span and this rule would be piling on.
            return null;
        }

        if (input.ToDisplayString() != BusMessageName)
        {
            return
                $"its input contract is '{input.ToDisplayString()}' and a delivery has only the " +
                "message to give it — declare it as Flow<BusMessage, TOut> and deserialise the " +
                "payload in a capability";
        }

        return IsDurable(attributes)
            ? null
            : "it does not declare ExecutionProfile.Durable, so nothing journals its instances, " +
              "the instance id a delivery derives is inert, and one message would start one flow " +
              "per delivery — declare Profile = ExecutionProfile.Durable";
    }

    /// <summary>Whether this attribute is one of the two bus triggers this build has a shape for.</summary>
    private static bool IsBusTrigger(AttributeData attribute) =>
        attribute.AttributeClass?.ToDisplayString() is { } name &&
        System.Array.IndexOf(BusTriggerAttributeNames, name) >= 0;

    /// <summary>
    /// Reports FLOWX1038 on each <c>[CronTrigger]</c> the host would have nothing to do with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Here rather than in the generator, for the reason FLOWX1025 is here.</strong>
    /// The generator's schedule pipeline works from collected models with no syntax attached, so
    /// it has nowhere to point; the analyzer has the attribute's own span. It is also the
    /// question worth answering on the keystroke that applies the attribute rather than when the
    /// generator next runs — the whole failure mode is a declaration that looks right.
    /// </para>
    /// <para>
    /// <strong>One report per attribute, not one per flow.</strong> A flow may declare several
    /// schedules and every one of them is unfireable for the same reason, so pointing at each is
    /// what makes "remove this or fix the flow" a decision the author can take per line.
    /// </para>
    /// </remarks>
    private static void ReportUnfireableSchedules(
        SymbolAnalysisContext context, INamedTypeSymbol type, ImmutableArray<AttributeData> attributes)
    {
        var reason = UnfireableReason(type, attributes);

        if (reason is null)
        {
            return;
        }

        foreach (var attribute in attributes)
        {
            if (attribute.AttributeClass?.ToDisplayString() != CronTriggerAttributeName)
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                FlowXDiagnostics.ScheduledFlowCannotBeFired,
                LocationOf(attribute, type, context.CancellationToken),
                FlowIdOf(type, attributes),
                reason));
        }
    }

    /// <summary>
    /// Why nothing could fire this flow's schedules, or <c>null</c> when something can.
    /// </summary>
    /// <remarks>
    /// The two reasons are checked in the order an author would repair them: a flow whose input
    /// is wrong cannot be started at all, and a flow whose profile is wrong would be started too
    /// often. Reporting both at once would give one line two fixes, and the first is the one
    /// that changes the flow's signature.
    /// </remarks>
    private static string? UnfireableReason(
        INamedTypeSymbol type, ImmutableArray<AttributeData> attributes)
    {
        if (!attributes.Any(static a => a.AttributeClass?.ToDisplayString() == CronTriggerAttributeName))
        {
            return null;
        }

        if (InputOf(type) is not { } input)
        {
            // The base type did not resolve, so C# is already reporting something more useful
            // about the same span and this rule would be piling on.
            return null;
        }

        if (input.ToDisplayString() != ScheduledFireName)
        {
            return
                $"its input contract is '{input.ToDisplayString()}' and a schedule has only an " +
                "occurrence to give it — declare it as Flow<ScheduledFire, TOut>";
        }

        return IsDurable(attributes)
            ? null
            : "it does not declare ExecutionProfile.Durable, so nothing journals its instances " +
              "and every node in the fleet would run every occurrence — declare " +
              "Profile = ExecutionProfile.Durable";
    }

    /// <summary>The <c>TIn</c> of the <c>Flow&lt;TIn, TOut&gt;</c> this type derives from.</summary>
    private static ITypeSymbol? InputOf(INamedTypeSymbol type)
    {
        for (var candidate = type.BaseType; candidate is not null; candidate = candidate.BaseType)
        {
            if (candidate.ConstructedFrom?.ToDisplayString() is { } name &&
                name.StartsWith(FlowBaseName + "<", System.StringComparison.Ordinal) &&
                candidate.TypeArguments.Length == 2)
            {
                return candidate.TypeArguments[0] is IErrorTypeSymbol ? null : candidate.TypeArguments[0];
            }
        }

        return null;
    }

    /// <summary>Whether <c>[Flow]</c> names <c>Durable</c>.</summary>
    /// <remarks>
    /// <c>ExecutionProfile.Durable</c> is <c>1</c>. Compared as the underlying value rather than
    /// by name because that is all attribute data carries, and spelled out here rather than
    /// derived for the reason <c>TriggerReader.ManifestKindName</c> gives about its own map:
    /// reordering the enum is a breaking change the compiler cannot see, and it should surface
    /// as a failing test rather than as a rule that quietly stops firing.
    /// </remarks>
    private static bool IsDurable(ImmutableArray<AttributeData> attributes) => attributes
        .Where(static a => a.AttributeClass?.ToDisplayString() == FlowAttributeName)
        .SelectMany(static a => a.NamedArguments)
        .Any(static pair => pair.Key == "Profile" && pair.Value.Value is int profile && profile == 1);

    /// <summary>The flow's declared id, or its type name when the attribute carries none.</summary>
    private static string FlowIdOf(INamedTypeSymbol type, ImmutableArray<AttributeData> attributes) =>
        attributes
            .Where(static a => a.AttributeClass?.ToDisplayString() == FlowAttributeName)
            .Where(static a => a.ConstructorArguments.Length > 0)
            .Select(static a => a.ConstructorArguments[0].Value as string)
            .FirstOrDefault(static id => !string.IsNullOrEmpty(id)) ?? type.Name;

    /// <summary>How loud to be, given who can fix it.</summary>
    /// <remarks>
    /// <para>
    /// An <strong>error</strong> when the trigger attribute is declared in this
    /// compilation: the fix is one line in a file the person reading the diagnostic owns,
    /// and a rule nobody has to obey is not a rule. The same escalation FLOWX1011 makes for
    /// a <c>Durable</c> flow, and for the same reason — the severity follows what the
    /// author can actually do about it.
    /// </para>
    /// <para>
    /// A <strong>warning</strong> when it arrives from a referenced assembly. The marker
    /// belongs on the attribute class, so a consumer of a plugin that has not added one
    /// cannot fix this in their own repository at all; erroring would make using a
    /// third-party transport fail their build over someone else's omission, which
    /// <c>17-Plugin-System.md §1</c> rules out. It stays a warning they can see, report
    /// upstream, and downgrade with a recorded reason.
    /// </para>
    /// </remarks>
    /// <param name="attributeClass">The trigger attribute that carries no marker.</param>
    private static DiagnosticSeverity SeverityFor(INamedTypeSymbol attributeClass) =>
        attributeClass.Locations.Any(static location => location.IsInSource)
            ? DiagnosticSeverity.Error
            : DiagnosticSeverity.Warning;

    /// <summary>The attribute's own span, falling back to the type it is applied to.</summary>
    /// <remarks>
    /// An attribute applied through metadata — from a referenced assembly's
    /// <c>[assembly: …]</c>, or on a partial declaration in a file this compilation only
    /// has symbols for — has no syntax reference. Pointing at the type is still actionable;
    /// <see cref="Location.None"/> would put the message in the build log with no file at
    /// all, which is where diagnostics go to be ignored.
    /// </remarks>
    private static Location LocationOf(
        AttributeData attribute, INamedTypeSymbol type, System.Threading.CancellationToken cancellationToken)
    {
        var syntax = attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken);

        return syntax?.GetLocation()
            ?? type.Locations.FirstOrDefault(static l => l.IsInSource)
            ?? Location.None;
    }
}
