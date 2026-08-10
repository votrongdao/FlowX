using System.Collections.Generic;
using System.Linq;
using FlowX.Compiler.Model;
using Microsoft.CodeAnalysis;

namespace FlowX.Compiler.Analysis;

/// <summary>
/// Reads the trigger attributes on a flow's class
/// (<a href="../../../docs/adr/ADR-0004-universal-trigger-model.md">ADR-0004</a>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this can and cannot see, stated plainly.</strong> A trigger's
/// <c>Kind</c> property is not attribute data — it is an abstract property that each
/// attribute overrides with an expression (<c>public override TriggerKind Kind =&gt;
/// TriggerKind.Http;</c>). From metadata that is executable code, not a value. So the kind
/// is declared a second time, as data, with <c>[TriggerKind(TriggerKind.Http)]</c> on the
/// attribute class: an enum constructor argument <em>is</em> attribute data and is
/// readable across an assembly boundary. <see cref="KindOf"/> is that read, and it walks
/// the attribute's base chain so a plugin with its own intermediate base declares the kind
/// once.
/// </para>
/// <para>
/// This is what makes ADR-0004's "one trigger abstraction for every transport" true for
/// transports FlowX does not ship. A third-party <c>TriggerAttribute</c> subclass carrying
/// the marker reaches the manifest with its kind; one without it is skipped, and
/// <c>TriggerDeclarationAnalyzer</c> reports <c>FLOWX1025</c> on exactly the attributes
/// <see cref="KindOf"/> returns <c>null</c> for — which is why that read lives here, beside
/// the switch it must agree with, rather than as a second rule in the analyzer.
/// </para>
/// <para>
/// <strong>Kind is general; argument shape is not.</strong> Knowing an attribute is
/// <c>Bus</c> says nothing about what its constructor arguments mean, so the switch in
/// <see cref="Shape"/> still recognises only the attributes <c>FlowX.Abstractions</c>
/// ships, whose shape is part of the platform contract. A plugin trigger publishes its
/// kind and nothing else — an honest partial record rather than an absence, and rather
/// than a guess at which positional argument is a topic. Projecting a plugin's arguments
/// generically is possible in principle (<c>AttributeConstructor.Parameters</c> carries
/// parameter names in metadata) but has nowhere to go: the manifest schema's
/// <c>trigger</c> object is <c>additionalProperties: false</c> over a closed property
/// list, which is an ADR-0005 question rather than a compiler one.
/// </para>
/// <para>
/// <strong>Two declarations that can disagree.</strong> <c>Kind =&gt;</c> is what the
/// runtime reads and <c>[TriggerKind]</c> is what the manifest publishes. For the five
/// built-ins a fitness function holds them equal. For an attribute that arrives as a
/// compiled reference nothing can: the property's value exists only once the getter runs,
/// and a generator does not run the code it compiles. The manifest therefore publishes the
/// declared marker, and says so.
/// </para>
/// <para>
/// <strong>A declared trigger is not the same as a bound one — for every kind but two.</strong>
/// This paragraph said "nothing yet turns these attributes into endpoint registrations —
/// the sample maps its route by hand in <c>Program.cs</c>". Both halves expired:
/// <c>EndpointEmitter</c> turns each <c>[HttpTrigger]</c> into a registration in
/// <c>FlowXEndpoints.g.cs</c>, and all three samples call the generated
/// <c>app.MapFlowX()</c> rather than mapping a route themselves. The registration and the
/// manifest's <c>triggers</c> block come from this one reading of the attribute, so for an
/// HTTP flow there is no second copy of the route to drift from the declared one. What is
/// still unasserted is the other direction: nothing stops a hand-written route reaching a
/// flow at an address it never declared.
/// </para>
/// <para>
/// <strong><c>Schedule</c> is the second, and it is bound the same way.</strong> <em>This
/// paragraph named it among the five that were declaration only, and said in those words
/// that "a flow declaring one of those declares an address nothing serves". That expired on
/// 2026-08-01.</em> <c>ScheduleEmitter</c> turns each <c>[CronTrigger]</c> into a
/// registration in <c>FlowXSchedules.g.cs</c>, <c>samples/workflow</c> calls the generated
/// <c>services.AddFlowXSchedules()</c>, and <c>FlowScheduleScan</c> fires it. The copy rule
/// is stricter here than for a route: the expression and the zone are taken off the
/// <see cref="TriggerModel"/> this reader produced for the manifest, because they are two of
/// the five values every node derives the instance id from
/// (<a href="../../../docs/adr/ADR-0031-an-occurrence-names-the-instance-it-starts.md">ADR-0031</a>)
/// — a second copy would not mislead a reader, it would split one schedule into two.
/// <c>FLOWX1038</c> refuses the two declarations that could not be fired.
/// </para>
/// <para>
/// <strong><c>Bus</c> is the third, and it is the one the model was hardest to keep.</strong>
/// <em>This paragraph named it first among the four that were declaration only, and said in those
/// words that "a flow declaring one of those declares an address nothing serves". That expired on
/// 2026-08-01.</em> <c>BusEmitter</c> turns each <c>[BusTrigger]</c> and <c>[KafkaTrigger]</c>
/// into a registration in <c>FlowXSubscriptions.g.cs</c>, and <c>FlowBusScan</c> drives an
/// <c>IBusConsumer</c> into the same <c>FlowEngine.ExecuteAsync</c> an HTTP request reaches. The
/// copy rule is the schedule's: the topic and the group are taken off the
/// <see cref="TriggerModel"/> this reader produced for the manifest, because they are two of the
/// five values every node derives a delivery's instance id from
/// (<a href="../../../docs/adr/ADR-0035-a-delivery-names-the-instance-it-starts.md">ADR-0035</a>)
/// — a second copy would not mislead a reader, it would make one message start two flows.
/// <c>FLOWX1039</c> refuses the two declarations that could not be consumed.
/// </para>
/// <para>
/// <em>This paragraph named <c>Agent</c> third among the kinds that were declaration only.
/// That expired on 2026-08-02.</em> <c>AgentToolEmitter</c> turns each <c>[AgentTrigger]</c>
/// into a binding in <c>FlowXAgentTools.g.cs</c>, and <c>FlowX.Mcp</c> serves
/// <c>tools/call</c> into the same <c>FlowEngine.ExecuteAsync</c> an HTTP request reaches.
/// The copy rule is <em>stricter</em> than the other three's rather than the same: a route, a
/// cron expression and a topic are copied off the <see cref="TriggerModel"/> this reader
/// produced, because a router, a scheduler and a consumer each need an address before any
/// manifest is read — and an agent tool has no address. Its name is the flow's own id and
/// everything else about it is <c>trigger.description</c>, <c>capability.authorization</c>
/// and <c>capability.sideEffects</c>, so nothing is copied at all and the published surface
/// is read back out of the manifest at run time.
/// </para>
/// <para>
/// <em>This paragraph named <c>Change</c> beside <c>Stream</c> among the kinds that were
/// declaration only. That expired on 2026-08-02.</em> <c>ChangeEmitter</c> turns each
/// <c>[ChangeTrigger]</c> into a registration in <c>FlowXChangeSubscriptions.g.cs</c>, and
/// <c>FlowChangeScan</c> drives an <c>IChangeFeed</c> over the outbox into the same
/// <c>FlowEngine.ExecuteAsync</c> an HTTP request reaches. The copy rule is the bus's: the
/// source and the group are taken off the <see cref="TriggerModel"/> this reader produced,
/// because they are two of the four values every node derives a change's instance id from
/// (<a href="../../../docs/adr/ADR-0049-a-change-names-the-instance-it-starts.md">ADR-0049</a>)
/// and two of the four the cursor is keyed on. <c>FLOWX1041</c> refuses the two declarations
/// that could not be observed.
/// </para>
/// <para>
/// <c>Stream</c> is still declaration only:
/// nothing binds it, so a flow declaring it declares an address nothing serves.
/// (<c>Manual</c> needs no binding, and <c>Cli</c>'s summary names
/// <c>flowx run</c>, which is not one of the CLI's verbs.) The manifest publishes the
/// declaration either way,
/// because it is the authored intent; that it can outrun what is bound is a property of
/// the current build, not of the model.
/// </para>
/// </remarks>
public static class TriggerReader
{
    private const string TriggerAttributeBase = "FlowX.TriggerAttribute";

    private const string TriggerKindMarker = "FlowX.TriggerKindAttribute";

    /// <summary>
    /// The trigger attributes whose constructor and named arguments this build knows how
    /// to project into the manifest, by full type name.
    /// </summary>
    /// <remarks>
    /// Membership here decides <em>detail</em>, never whether a trigger is published at
    /// all: that is <see cref="KindOf"/>'s answer, and an attribute outside this list still
    /// reaches the manifest with its declared kind.
    /// <c>EveryTriggerAttributeTheAbstractionShipsHasAKnownShape</c> reflects over
    /// <c>FlowX.Abstractions</c> and fails if a further attribute is added without being
    /// added here, which would otherwise publish a bare kind for an attribute whose shape
    /// is part of the platform contract.
    /// </remarks>
    private static readonly string[] KnownShapes =
    [
        "FlowX.AgentTriggerAttribute",
        "FlowX.BusTriggerAttribute",
        "FlowX.ChangeTriggerAttribute",
        "FlowX.CronTriggerAttribute",
        "FlowX.HttpTriggerAttribute",
        "FlowX.KafkaTriggerAttribute",
        "FlowX.RabbitMqTriggerAttribute",
        "FlowX.ServiceBusTriggerAttribute",
        "FlowX.StreamTriggerAttribute",
    ];

    /// <summary>The full type names of the trigger attributes whose arguments this build projects.</summary>
    public static IReadOnlyList<string> AttributesWithKnownShape => KnownShapes;

    /// <summary>Every trigger the type declares, in attribute order.</summary>
    /// <param name="flow">The flow's class symbol.</param>
    public static IReadOnlyList<TriggerModel> Read(INamedTypeSymbol? flow)
    {
        if (flow is null)
        {
            return System.Array.Empty<TriggerModel>();
        }

        var triggers = new List<TriggerModel>();

        foreach (var attribute in flow.GetAttributes())
        {
            if (!IsTrigger(attribute.AttributeClass))
            {
                continue;
            }

            var model = ReadOne(attribute);

            if (model is not null)
            {
                triggers.Add(model);
            }
        }

        return triggers;
    }

    /// <summary>
    /// What each <c>[CronTrigger]</c> on the type declares that the manifest does not carry.
    /// </summary>
    /// <param name="flow">The flow's class symbol.</param>
    /// <remarks>
    /// <para>
    /// <strong>This reads no address.</strong> The cron expression comes back only as the key
    /// that joins one of these to the <see cref="TriggerModel"/> <see cref="Read"/> produced, and
    /// the registration takes its expression and its zone from that model — the arrangement
    /// <c>EndpointEmitter</c> has with a route, and the arrangement that makes a declared
    /// schedule and a fired one the same declaration
    /// (<a href="../../../docs/adr/ADR-0031-an-occurrence-names-the-instance-it-starts.md">ADR-0031</a>).
    /// </para>
    /// <para>
    /// Every property <c>[CronTrigger]</c> declares beside its address is here, because the
    /// runtime now reads all four: what happens to a firing that is late, whether an occurrence
    /// is one instance or one per tenant, what happens when the previous run has not finished,
    /// and how wide a window the firing is released within. <c>Overlap</c> and <c>Jitter</c>
    /// reached nothing until the sweep learned to ask both questions.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ScheduleDeclaration> ReadSchedules(INamedTypeSymbol? flow)
    {
        if (flow is null)
        {
            return System.Array.Empty<ScheduleDeclaration>();
        }

        var schedules = new List<ScheduleDeclaration>();

        foreach (var attribute in flow.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != "FlowX.CronTriggerAttribute" ||
                Positional(attribute, 0) is not { } cron)
            {
                continue;
            }

            schedules.Add(new ScheduleDeclaration(
                cron,
                MissedFireName(attribute),
                PerTenantFlag(attribute),
                OverlapName(attribute),
                Named(attribute, "Jitter")));
        }

        return schedules;
    }

    /// <summary>
    /// The part of each <c>[StreamTrigger]</c> the manifest does not publish.
    /// </summary>
    /// <param name="flow">The flow's type symbol, or null.</param>
    /// <returns>One declaration per stream trigger, in declaration order.</returns>
    /// <remarks>
    /// <para>
    /// <see cref="ReadSchedules"/>'s arrangement and its reason: the window, the lateness, the
    /// checkpoint interval and the parallelism are what this release's runtime reads, and none of
    /// them is address or admission, so they travel beside the <see cref="TriggerModel"/> rather
    /// than on it (ADR-0034's rule, one transport over).
    /// </para>
    /// <para>
    /// <strong>Every value is carried verbatim, including one the engine will refuse.</strong>
    /// <c>Window = "session:5m"</c> is read into a declaration here and reported by
    /// <c>FLOWX1042</c>; discarding it at the reader would leave the analyzer nothing to name and
    /// would make the generated registration silently disagree with the source.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<StreamDeclaration> ReadStreams(INamedTypeSymbol? flow)
    {
        if (flow is null)
        {
            return System.Array.Empty<StreamDeclaration>();
        }

        var streams = new List<StreamDeclaration>();

        foreach (var attribute in flow.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != "FlowX.StreamTriggerAttribute" ||
                Positional(attribute, 0) is not { } source)
            {
                continue;
            }

            streams.Add(new StreamDeclaration(
                source,
                Named(attribute, "Window") ?? string.Empty,

                // The attribute's own defaults, repeated because an argument that was not written
                // and one written as its default are the same declaration and must generate the
                // same registration.
                Named(attribute, "Lateness") ?? "PT0S",
                Named(attribute, "Checkpoint") ?? "PT5S",
                NamedInt(attribute, "Parallelism") ?? 1));
        }

        return streams;
    }

    private static int? NamedInt(AttributeData attribute, string name)
    {
        foreach (var pair in attribute.NamedArguments)
        {
            if (pair.Key == name && pair.Value.Value is int value)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Maps <c>MissedFirePolicy</c>'s underlying value back to its name.
    /// </summary>
    /// <remarks>
    /// Spelled out rather than derived, for the reason <see cref="KindName"/> gives: reordering
    /// the enum is a breaking change the compiler cannot see here, and it should surface as a
    /// failing test rather than as generated code that registers the wrong behaviour. The
    /// default matches the attribute's own — <c>RunOnce</c> — so omitting the argument and
    /// writing it produce the same registration.
    /// </remarks>
    private static string MissedFireName(AttributeData attribute)
    {
        var declared = attribute.NamedArguments
            .Where(pair => pair.Key == "MissedFire")
            .Select(pair => pair.Value.Value)
            .FirstOrDefault();

        return declared switch
        {
            0 => "Skip",
            2 => "RunAll",
            _ => "RunOnce",
        };
    }

    /// <summary>
    /// Maps <c>OverlapPolicy</c>'s underlying value back to its name.
    /// </summary>
    /// <remarks>
    /// <see cref="MissedFireName"/>'s map and its reason. The default matches the attribute's
    /// own — <c>Skip</c> — so omitting the argument and writing it produce the same
    /// registration, which is what makes the default a real default rather than a comment.
    /// </remarks>
    private static string OverlapName(AttributeData attribute)
    {
        var declared = attribute.NamedArguments
            .Where(pair => pair.Key == "Overlap")
            .Select(pair => pair.Value.Value)
            .FirstOrDefault();

        return declared switch
        {
            1 => "Queue",
            2 => "Concurrent",
            _ => "Skip",
        };
    }

    /// <summary>Whether the declaration asked for one firing per tenant.</summary>
    /// <remarks>
    /// Read from the named argument rather than defaulted to the deployment's isolation level,
    /// which the compiler cannot see and which is a run-time setting anyway: whether a schedule
    /// is per tenant is the author's statement about the <em>work</em>, and a nightly
    /// reconciliation is per tenant on a host that isolates and still per tenant on one that
    /// does not — where the directory is empty and the fan-out fires nothing.
    /// </remarks>
    private static bool PerTenantFlag(AttributeData attribute) => attribute.NamedArguments
        .Where(pair => pair.Key == "PerTenant")
        .Select(pair => pair.Value.Value)
        .FirstOrDefault() is true;

    /// <summary>Whether an attribute derives from <c>FlowX.TriggerAttribute</c>.</summary>
    /// <remarks>
    /// Public because <c>TriggerDeclarationAnalyzer</c> asks the same question and must
    /// get the same answer: it reports on what this reader skipped, so the two cannot be
    /// allowed to disagree about what a trigger is.
    /// </remarks>
    /// <param name="attributeClass">The applied attribute's type, or <c>null</c>.</param>
    public static bool IsTrigger(INamedTypeSymbol? attributeClass)
    {
        for (var type = attributeClass?.BaseType; type is not null; type = type.BaseType)
        {
            if (type.ToDisplayString() == TriggerAttributeBase)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The transport family the attribute declares through <c>[TriggerKind]</c>, spelled as
    /// the manifest schema's <c>kind</c> enum spells it — or <c>null</c> when it declares
    /// none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Walks the base chain, because <c>ISymbol.GetAttributes</c> returns only what is
    /// applied to that symbol: Roslyn does not apply <c>Inherited = true</c> to it. A plugin
    /// that factors shared members into <c>abstract class BusTriggerAttribute</c> therefore
    /// marks the base once and every attribute derived from it inherits the declaration —
    /// which is how the marker behaves at run time, and a surprising place for the two to
    /// differ.
    /// </para>
    /// <para>
    /// <c>null</c> for a value outside <c>TriggerKind</c> as well as for no marker at all.
    /// A cast integer is not a declared kind, and inventing a name for it would put a value
    /// in the manifest that the schema's closed <c>kind</c> enum rejects; FLOWX1025 covers
    /// it alongside the undeclared case, which is where an author looking for the cause
    /// will be told to look.
    /// </para>
    /// </remarks>
    /// <param name="attributeClass">The applied attribute's type, or <c>null</c>.</param>
    public static string? KindOf(INamedTypeSymbol? attributeClass)
    {
        for (var type = attributeClass; type is not null; type = type.BaseType)
        {
            foreach (var marker in type.GetAttributes())
            {
                if (marker.AttributeClass?.ToDisplayString() != TriggerKindMarker ||
                    marker.ConstructorArguments.Length != 1)
                {
                    continue;
                }

                var name = KindName(marker.ConstructorArguments[0].Value);

                if (name is not null)
                {
                    return name;
                }
            }
        }

        return null;
    }

    /// <summary>The manifest name of a <c>TriggerKind</c> value, or <c>null</c> if it has none.</summary>
    /// <remarks>
    /// Exposed for the fitness function that checks this map against the enum and against
    /// the schema. Neither is reachable from the generator — it targets netstandard2.0 and
    /// cannot reference <c>FlowX.Abstractions</c> — so the check lives in a test, and the
    /// test needs the map.
    /// </remarks>
    /// <param name="value">The enum's underlying value.</param>
    public static string? ManifestKindName(int value) => KindName(value);

    /// <summary>
    /// Maps <c>TriggerKind</c>'s underlying value back to its name.
    /// </summary>
    /// <remarks>
    /// Spelled out rather than derived from the enum symbol, for the reason
    /// <c>CapabilityReader</c> gives about <c>Authorization</c>, and one more specific to
    /// this enum: these names are also the manifest schema's closed <c>kind</c> enum.
    /// Deriving the name from metadata would let a new <c>TriggerKind</c> member reach the
    /// manifest as a value the schema rejects, discovered by whoever validated the
    /// document. Written out, adding a member fails
    /// <c>EveryTriggerKindHasAManifestNameTheSchemaAccepts</c> instead.
    /// </remarks>
    private static string? KindName(object? value) => value switch
    {
        0 => "Manual",
        1 => "Http",
        2 => "Bus",
        3 => "Schedule",
        4 => "Stream",
        5 => "Change",
        6 => "Agent",
        7 => "Cli",
        _ => null,
    };

    private static TriggerModel? ReadOne(AttributeData attribute)
    {
        var kind = KindOf(attribute.AttributeClass);

        // No readable declaration of the family: skipped rather than guessed at, and
        // reported — see TriggerDeclarationAnalyzer.
        return kind is null ? null : Shape(attribute, kind) ?? new TriggerModel(kind);
    }

    /// <summary>
    /// The declared address of one of the attributes whose shape is part of the
    /// platform contract, or <c>null</c> for an attribute whose arguments this build cannot
    /// interpret.
    /// </summary>
    /// <remarks>
    /// The kind comes from the marker rather than from this switch even for the built-ins,
    /// so the reader has exactly one answer to "which family is this" and the two cannot
    /// drift apart inside it.
    /// </remarks>
    /// <summary>
    /// The fully qualified type a trigger's <c>Decode</c> names, or <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Read here and carried on the model so the emitter can call it, and dropped by
    /// <c>ManifestWriter</c> so it never reaches the published document — see
    /// <c>TriggerModel.Decoder</c> for why those are two different decisions rather than an
    /// oversight. Whether the named type can actually decode is <c>FLOWX1051</c>'s question; a
    /// reader that judged it would be a second copy of that rule.
    /// </remarks>
    private static string? Decoder(AttributeData attribute) =>
        attribute.NamedArguments
            .FirstOrDefault(pair => pair.Key == "Decode")
            .Value.Value is INamedTypeSymbol decoder
            ? decoder.ToDisplayString()
            : null;

    private static TriggerModel? Shape(AttributeData attribute, string kind) =>
        attribute.AttributeClass?.ToDisplayString() switch
        {
            "FlowX.HttpTriggerAttribute" => new TriggerModel(
                kind,
                method: Positional(attribute, 0),
                route: Positional(attribute, 1),
                idempotent: Flag(attribute, "Idempotent")),

            // The transport-neutral bus declaration publishes no `transport`, which is the honest
            // reading of "this flow consumes topic T as group G, on whatever bus the host wired"
            // (ADR-0039). TriggerModel.Transport has always allowed for it.
            "FlowX.BusTriggerAttribute" => new TriggerModel(
                kind,
                topic: Positional(attribute, 0),
                group: Named(attribute, "Group"),
                decoder: Decoder(attribute)),

            "FlowX.KafkaTriggerAttribute" => new TriggerModel(
                kind,
                transport: "kafka",
                topic: Positional(attribute, 0),
                group: Named(attribute, "Group")),

            // The other two transport-named declarations. Same shape as Kafka's and the same
            // reason for existing: the transport reaches the manifest and FlowBusCatalog refuses
            // a host whose IBusConsumer answers a different family, so a subscription is never
            // quietly served by the wrong broker.
            "FlowX.RabbitMqTriggerAttribute" => new TriggerModel(
                kind,
                transport: "rabbitmq",
                topic: Positional(attribute, 0),
                group: Named(attribute, "Group")),

            "FlowX.ServiceBusTriggerAttribute" => new TriggerModel(
                kind,
                transport: "azure-servicebus",
                topic: Positional(attribute, 0),
                group: Named(attribute, "Group")),

            // A change subscription's address is an event type and a group, which are the two
            // fields a bus subscription already publishes — so it reaches the manifest through
            // `topic` and `group` and adds no property to a schema that is
            // additionalProperties: false (ADR-0047 decision 4).
            "FlowX.ChangeTriggerAttribute" => new TriggerModel(
                kind,
                topic: Positional(attribute, 0),
                group: Named(attribute, "Group")),

            "FlowX.CronTriggerAttribute" => new TriggerModel(
                kind,
                cron: Positional(attribute, 0),
                timeZone: Named(attribute, "TimeZone") ?? "UTC"),

            "FlowX.StreamTriggerAttribute" => new TriggerModel(
                kind,
                topic: Positional(attribute, 0)),

            "FlowX.AgentTriggerAttribute" => new TriggerModel(
                kind,
                description: Named(attribute, "Description"),
                confirmation: ConfirmationName(attribute)),

            // A trigger attribute this build has no shape for. Its kind still publishes.
            _ => null,
        };

    private static string? Positional(AttributeData attribute, int index) =>
        attribute.ConstructorArguments.Length > index
            ? attribute.ConstructorArguments[index].Value as string
            : null;

    private static string? Named(AttributeData attribute, string name) => attribute.NamedArguments
        .Where(pair => pair.Key == name)
        .Select(pair => pair.Value.Value as string)
        .FirstOrDefault();

    /// <summary>Reads a boolean named argument, defaulting to the attribute's own default.</summary>
    /// <remarks>
    /// Returns <c>false</c> rather than <c>null</c> when absent: <c>Idempotent</c> is a
    /// <c>bool</c> with a default of <c>false</c>, so "not written" and "written false"
    /// are the same declaration and must produce the same manifest.
    /// </remarks>
    private static bool Flag(AttributeData attribute, string name) => attribute.NamedArguments
        .Where(pair => pair.Key == name)
        .Select(pair => pair.Value.Value is bool flag && flag)
        .FirstOrDefault();

    /// <summary>
    /// Maps <c>ConfirmationMode</c>'s underlying value back to its name.
    /// </summary>
    /// <remarks>
    /// Spelled out rather than derived, for the reason <c>CapabilityReader</c> gives about
    /// <c>Authorization</c>: reordering the enum is a breaking change the compiler cannot
    /// see here, and it should surface as a failing test rather than as a silently wrong
    /// manifest. The default matches the attribute's own —
    /// <c>RequiredForSideEffects</c> — so omitting the argument and writing it produce the
    /// same document.
    /// </remarks>
    private static string ConfirmationName(AttributeData attribute)
    {
        var declared = attribute.NamedArguments
            .Where(pair => pair.Key == "Confirmation")
            .Select(pair => pair.Value.Value)
            .FirstOrDefault();

        return declared switch
        {
            0 => "Never",
            2 => "Always",
            _ => "RequiredForSideEffects",
        };
    }
}
