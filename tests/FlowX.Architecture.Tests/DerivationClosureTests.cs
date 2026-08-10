using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Shouldly;
using Xunit;

namespace FlowX.Architecture.Tests;

/// <summary>
/// The rule that every externally addressable surface is derived from the compiled graph,
/// rather than written down a second time in a deployment's configuration.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What the rule is.</strong> An application declares one address per trigger — the
/// route on <c>[HttpTrigger]</c>, the topic on <c>[BusTrigger]</c>, the expression on
/// <c>[ScheduleTrigger]</c> — and every other address the deployment needs is a function of
/// that one plus the graph. The signal endpoint is
/// <c>{route}/{instanceId:guid}/signals/{identity}</c>
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0022-http-shape-of-a-suspending-flow.md">ADR-0022</a>);
/// the dead-letter destination is the source address plus a suffix
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0073-a-dead-letter-destination-stays-derived-even-where-the-broker-has-one.md">ADR-0073</a>);
/// the address a schedule calls is the flow's own
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0034-the-manifest-publishes-a-schedules-address.md">ADR-0034</a>).
/// None of them is configured, so none of them can disagree with the flow it belongs to.
/// </para>
/// <para>
/// <strong>Why it needs a gate rather than a convention.</strong> The rule is invisible in
/// the diff that breaks it. Adding <c>public string PlaceOrderTopic { get; init; } =
/// "order.place";</c> to a transport's options record is two lines, reads as a helpful
/// convenience, and is the whole failure: from that commit on, the flow's name and the topic
/// it is reached at are two facts maintained in two places, and the second one is not in the
/// manifest, not in <c>flowx diff</c>, and not renamed when the flow is.
/// </para>
/// <para>
/// <strong>Both facts below are about what is missing</strong>, which is the hard direction
/// to test. The first asks whether every trigger kind that has an address has something that
/// derives it; the second asks whether any address that should have been derived was written
/// into configuration instead.
/// </para>
/// </remarks>
public sealed class DerivationClosureTests
{
    /// <summary>The emitter that derives each addressable trigger kind's surface.</summary>
    /// <remarks>
    /// By file rather than by type, because <c>FlowX.Compiler</c> targets
    /// <c>netstandard2.0</c> and <c>RoslynComponentsReferenceNoRuntimeAssemblies</c> forbids
    /// this project from referencing it at run time. A file that has to exist is a weaker
    /// assertion than a type that has to emit, and it is the strongest one available from
    /// here; what it catches is the case worth catching — a trigger kind arriving with no
    /// derivation at all.
    /// </remarks>
    private static readonly Dictionary<string, string> EmitterFor = new(StringComparer.Ordinal)
    {
        [nameof(TriggerKind.Http)] = "EndpointEmitter.cs",
        [nameof(TriggerKind.Bus)] = "BusEmitter.cs",
        [nameof(TriggerKind.Schedule)] = "ScheduleEmitter.cs",
        [nameof(TriggerKind.Stream)] = "StreamEmitter.cs",
        [nameof(TriggerKind.Change)] = "ChangeEmitter.cs",
        [nameof(TriggerKind.Agent)] = "AgentToolEmitter.cs",
    };

    /// <summary>The trigger kinds that have no externally addressable surface, and why.</summary>
    /// <remarks>
    /// An exemption is a claim about a trigger, so it is written next to the trigger and read
    /// back in the failure message. A kind that is neither here nor in
    /// <see cref="EmitterFor"/> fails, which is the point: the next kind added has to say
    /// which of the two it is.
    /// </remarks>
    private static readonly Dictionary<string, string> WithoutAnAddress = new(StringComparer.Ordinal)
    {
        [nameof(TriggerKind.Manual)] =
            "in-process: the caller holds the plan, so there is no address to publish or to derive",
        [nameof(TriggerKind.Cli)] =
            "`flowx run <flow>` addresses a flow by the id the manifest already publishes, "
            + "so the derivation is the manifest itself rather than a generated registration",
    };

    /// <summary>
    /// Emit files that derive something other than a trigger's address, so their absence from
    /// <see cref="EmitterFor"/> is correct rather than an oversight.
    /// </summary>
    private static readonly string[] NotATriggerSurface =
    [
        "FlowEmitter.cs",                    // the execution plan, which has no address
        "CapabilityRegistrationEmitter.cs",  // container registrations
        "HostWiringEmitter.cs",              // the host's own composition
        "ManifestWriter.cs",                 // the graph every other emitter derives from
        "SourceWriter.cs",                   // the writer the others share
    ];

    /// <summary>
    /// Every trigger kind that reaches an external caller has an emitter that derives its
    /// address, and every emitter belongs to a kind.
    /// </summary>
    /// <remarks>
    /// The second half is what makes this more than a checklist. A new emitter added without
    /// a trigger kind is an address the compiler produces that nothing in the trigger model
    /// accounts for; a new trigger kind added without an emitter is an address a deployment
    /// will end up writing by hand. The test fails on both, and the failure names which.
    /// </remarks>
    [Fact]
    public void EveryTriggerKindThatHasAnAddressIsEmitted()
    {
        var emitDirectory = new DirectoryInfo(
            Path.Combine(RepositoryLayout.Root.FullName, "src", "FlowX.Compiler", "Emit"));

        emitDirectory.Exists.ShouldBeTrue(
            $"{emitDirectory.FullName} is not there, so this gate inspected nothing. The "
            + "emitters moved and the rule stopped being checked at the same moment.");

        var unaccounted = Enum.GetNames<TriggerKind>()
            .Where(kind => !EmitterFor.ContainsKey(kind) && !WithoutAnAddress.ContainsKey(kind))
            .ToList();

        unaccounted.ShouldBeEmpty(
            "A trigger kind is neither derived by an emitter nor recorded as having no "
            + "address. Until it is one of the two, a deployment reaching it has to be told "
            + "the address by hand, which is the second source of truth this rule exists to "
            + "prevent: " + string.Join(", ", unaccounted));

        var missing = EmitterFor
            .Where(pair => !File.Exists(Path.Combine(emitDirectory.FullName, pair.Value)))
            .Select(pair => $"{pair.Key} -> {pair.Value}")
            .ToList();

        missing.ShouldBeEmpty(
            "A trigger kind names an emitter that is not there. Either the emitter was "
            + "renamed and this map was not, or the derivation was removed and the address "
            + "is now a deployment's problem: " + string.Join(", ", missing));

        var orphans = emitDirectory.GetFiles("*.cs")
            .Select(file => file.Name)
            .Where(name => !EmitterFor.ContainsValue(name) && !NotATriggerSurface.Contains(name))
            .ToList();

        orphans.ShouldBeEmpty(
            "An emitter derives a surface no trigger kind accounts for. Either it belongs to "
            + "a kind and this map is short, or it derives something that is not an address "
            + "and belongs in NotATriggerSurface — say which, because an unclassified "
            + "emitter is an address nobody has decided the rules for: "
            + string.Join(", ", orphans));
    }

    /// <summary>
    /// No transport's configuration names a flow, a capability or an event.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A transport's options record carries the addresses of the <em>deployment</em> — a
    /// broker's bootstrap servers, the root topic, the prefix a queue name is built from, the
    /// suffix a dead-letter destination takes. Every one of those is a root or a shape, and a
    /// root is legitimately a deployment's to choose. What is never a deployment's to choose
    /// is which flow answers which address, because that fact is in the graph.
    /// </para>
    /// <para>
    /// So the check is a containment one: no string literal anywhere in a transport's options
    /// type may be, or contain as a dotted segment, the id of a flow, the id of a capability
    /// or the type of an event that a real compilation produced. The manifest read here is
    /// <c>samples/ecommerce/flowx.manifest.baseline.json</c> — the only committed manifest in
    /// the repository that a compiler actually wrote, which is what makes the subject list
    /// real names rather than names a test invented.
    /// </para>
    /// <para>
    /// <strong>The known-good literals are the argument.</strong> <c>flowx.events</c>,
    /// <c>flowx.events.dead</c>, <c>flowx</c>, <c>.dead</c> and <c>:</c> all pass and should:
    /// each is a root or a separator, and none of them changes when a flow is renamed. That is
    /// the line this gate draws.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoTransportOptionNamesAnApplicationConcept()
    {
        var concepts = ApplicationConcepts();

        concepts.Count.ShouldBeGreaterThan(
            0,
            "The baseline manifest yielded no flow, capability or event, so this gate had "
            + "nothing to look for and would pass against any configuration at all.");

        var findings = new List<string>();
        var inspected = 0;

        foreach (var assembly in CompiledAssemblies.ShippingAssemblies.Where(IsTransport))
        {
            using var module = CompiledAssemblies.Read(assembly);

            foreach (var type in IlSurvey.AllTypes(module)
                         .Where(candidate => candidate.Name.EndsWith("Options", StringComparison.Ordinal)))
            {
                inspected++;

                foreach (var literal in Literals(type))
                {
                    findings.AddRange(
                        concepts
                            .Where(concept => Names(literal, concept))
                            .Select(concept =>
                                $"{assembly}: {type.Name} carries the literal \"{literal}\", which "
                                + $"names \"{concept}\""));
                }
            }
        }

        inspected.ShouldBeGreaterThan(
            0,
            "No options type was found in any transport assembly. Either the transports "
            + "stopped being built, or the naming convention this gate reads changed — "
            + "either way it inspected nothing.");

        findings.ShouldBeEmpty(
            "A transport's configuration names something the graph already names. From here "
            + "on, that address and the flow it belongs to are two facts in two places: "
            + "renaming the flow does not rename the address, `flowx diff` cannot see the "
            + "change, and the manifest is no longer a complete description of how the "
            + "application is reached." + Environment.NewLine
            + string.Join(Environment.NewLine, findings));
    }

    /// <summary>The assemblies that hold a transport's configuration.</summary>
    /// <remarks>
    /// Everything under <c>plugins/</c>, discovered rather than listed, so a transport added
    /// next month is checked without anybody remembering to add it here.
    /// </remarks>
    private static bool IsTransport(string assembly) =>
        CompiledAssemblies.ProjectDirectory(assembly) is { } directory
        && directory.Parent?.Name == "plugins";

    /// <summary>Every string literal a type's own code contains.</summary>
    private static IEnumerable<string> Literals(TypeDefinition type) =>
        type.Methods
            .Where(method => method.HasBody)
            .SelectMany(method => method.Body.Instructions)
            .Where(instruction => instruction.OpCode == OpCodes.Ldstr)
            .Select(instruction => instruction.Operand as string)
            .Where(literal => !string.IsNullOrEmpty(literal))
            .Select(literal => literal!)
            .Distinct(StringComparer.Ordinal);

    /// <summary>
    /// Whether a literal is an application concept, or is built from one.
    /// </summary>
    /// <remarks>
    /// Segment-aware rather than a substring test in either direction. A plain
    /// <c>Contains</c> would report <c>flowx.events</c> for an event called <c>events</c>,
    /// and an equality test would miss <c>order.placed.dead</c> — which is exactly the
    /// hand-written derivation the gate is looking for.
    /// </remarks>
    private static bool Names(string literal, string concept)
    {
        var segments = literal.Split('.', '/', ':', '-', '_');

        return concept
            .Split('.')
            .All(part => segments.Contains(part, StringComparer.OrdinalIgnoreCase))
            && concept.Contains('.', StringComparison.Ordinal);
    }

    /// <summary>The flow ids, capability ids and event types a real compilation produced.</summary>
    private static List<string> ApplicationConcepts()
    {
        var baseline = new FileInfo(Path.Combine(
            RepositoryLayout.Root.FullName, "samples", "ecommerce", "flowx.manifest.baseline.json"));

        baseline.Exists.ShouldBeTrue(
            $"{baseline.FullName} is not there. It is the only committed manifest a compiler "
            + "wrote, and without it this gate has no real names to look for.");

        using var document = JsonDocument.Parse(File.ReadAllText(baseline.FullName));

        return
        [
            .. Values(document.RootElement, "flows", "id"),
            .. Values(document.RootElement, "capabilities", "id"),
            .. Values(document.RootElement, "events", "type"),
        ];
    }

    private static IEnumerable<string> Values(JsonElement root, string collection, string property) =>
        root.TryGetProperty(collection, out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray()
                .Where(item => item.TryGetProperty(property, out _))
                .Select(item => item.GetProperty(property).GetString())
                .Where(value => !string.IsNullOrEmpty(value))
                .Select(value => value!)
            : [];
}
