using System.Text.Json.Serialization;

namespace FlowX.Cli.Manifest;

/// <summary>The manifest, as the CLI reads it.</summary>
/// <remarks>
/// <para>
/// A separate set of types from the ones the compiler writes, on purpose. The CLI is
/// the first consumer of the manifest that is not the compiler, so modelling it
/// independently is what proves the document is a contract rather than an internal
/// serialisation format. If these types ever have to import something from
/// <c>FlowX.Compiler</c> to make sense of the file, the manifest has stopped being
/// self-describing.
/// </para>
/// <para>
/// Everything is nullable and nothing is required. A tool that refuses to render a
/// diagram because a manifest from a newer compiler carries a field it does not know
/// is a tool people stop upgrading.
/// </para>
/// </remarks>
public sealed class ManifestDocument
{
    /// <summary>Schema version the document was written against.</summary>
    [JsonPropertyName("schemaVersion")]
    public string? SchemaVersion { get; set; }

    /// <summary>The application that produced it.</summary>
    [JsonPropertyName("application")]
    public ManifestApplication? Application { get; set; }

    /// <summary>Every flow in the application.</summary>
    [JsonPropertyName("flows")]
    public List<ManifestFlow> Flows { get; set; } = [];

    /// <summary>Every capability, deduplicated by identity and version.</summary>
    [JsonPropertyName("capabilities")]
    public List<ManifestCapability> Capabilities { get; set; } = [];

    /// <summary>Every event type the application declares.</summary>
    [JsonPropertyName("events")]
    public List<ManifestEvent> Events { get; set; } = [];
}

/// <summary>Application identity.</summary>
public sealed class ManifestApplication
{
    /// <summary>Application name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Application version.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

/// <summary>One flow.</summary>
public sealed class ManifestFlow
{
    /// <summary>Business identity.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>SemVer.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>Execution profile.</summary>
    [JsonPropertyName("profile")]
    public string? Profile { get; set; }

    /// <summary>ISO-8601 deadline.</summary>
    [JsonPropertyName("deadline")]
    public string? Deadline { get; set; }

    /// <summary>The contract the flow accepts.</summary>
    [JsonPropertyName("input")]
    public ManifestTypeRef? Input { get; set; }

    /// <summary>The contract the flow returns.</summary>
    [JsonPropertyName("output")]
    public ManifestTypeRef? Output { get; set; }

    /// <summary>How the flow can be started from outside the process.</summary>
    [JsonPropertyName("triggers")]
    public List<ManifestTrigger> Triggers { get; set; } = [];

    /// <summary>Steps, in execution order.</summary>
    [JsonPropertyName("steps")]
    public List<ManifestStep> Steps { get; set; } = [];

    /// <summary>Events the flow publishes.</summary>
    [JsonPropertyName("emits")]
    public List<string> Emits { get; set; } = [];
}

/// <summary>A contract type, with the members of it that carry secrets.</summary>
public sealed class ManifestTypeRef
{
    /// <summary>Fully-qualified CLR type name.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>
    /// Members carrying <c>[Sensitive]</c>, present only when the contract has at least one.
    /// </summary>
    [JsonPropertyName("sensitive")]
    public List<string> Sensitive { get; set; } = [];
}

/// <summary>One way of starting a flow.</summary>
public sealed class ManifestTrigger
{
    /// <summary>Manual, Http, Bus, Schedule, Stream, Change, Agent or Cli.</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    /// <summary>HTTP verb, for <c>Http</c> triggers.</summary>
    [JsonPropertyName("method")]
    public string? Method { get; set; }

    /// <summary>HTTP route, for <c>Http</c> triggers.</summary>
    [JsonPropertyName("route")]
    public string? Route { get; set; }

    /// <summary>Broker family, for <c>Bus</c> and <c>Stream</c> triggers.</summary>
    [JsonPropertyName("transport")]
    public string? Transport { get; set; }

    /// <summary>Topic or queue, for <c>Bus</c> and <c>Stream</c> triggers.</summary>
    [JsonPropertyName("topic")]
    public string? Topic { get; set; }

    /// <summary>Consumer group, for <c>Bus</c> triggers.</summary>
    [JsonPropertyName("group")]
    public string? Group { get; set; }

    /// <summary>Cron expression, for <c>Schedule</c> triggers.</summary>
    [JsonPropertyName("cron")]
    public string? Cron { get; set; }

    /// <summary>IANA time zone the cron expression is evaluated in.</summary>
    [JsonPropertyName("timeZone")]
    public string? TimeZone { get; set; }

    /// <summary>
    /// Whether the transport demands an idempotency key before the flow is created.
    /// </summary>
    /// <remarks>
    /// Nullable, and the distinction is used: <c>null</c> means the trigger kind has no
    /// such notion, which is not the same as declaring that no key is required.
    /// </remarks>
    [JsonPropertyName("idempotent")]
    public bool? Idempotent { get; set; }

    /// <summary>The tool description an agent trigger shows to the model.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>Never, RequiredForSideEffects or Always, for <c>Agent</c> triggers.</summary>
    [JsonPropertyName("confirmation")]
    public string? Confirmation { get; set; }
}

/// <summary>One step of a flow.</summary>
public sealed class ManifestStep
{
    /// <summary>Position in the graph.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Step kind.</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    /// <summary>Capability invoked, as <c>id@version</c>.</summary>
    [JsonPropertyName("capability")]
    public string? Capability { get; set; }

    /// <summary>Compensation registered, as <c>id@version</c>.</summary>
    [JsonPropertyName("compensation")]
    public string? Compensation { get; set; }

    /// <summary>Event published.</summary>
    [JsonPropertyName("event")]
    public string? Event { get; set; }

    /// <summary>
    /// How a <c>Parallel</c> step joins its branches: <c>AllMustSucceed</c>,
    /// <c>AllSettled</c>, <c>FirstSuccess</c> or <c>Quorum</c>. Absent on every other kind,
    /// and on a fork whose strategy the compiler could not read statically.
    /// </summary>
    /// <remarks>
    /// A quorum's <em>size</em> is deliberately not published — it comes from an arbitrary
    /// expression in the flow's source, and the manifest carries structure and never
    /// values. So a reader learns that the fork waits for a quorum and has to read the
    /// flow to learn how large it is.
    /// </remarks>
    [JsonPropertyName("merge")]
    public string? Merge { get; set; }

    /// <summary>
    /// The business identity of the flow a <c>SubFlow</c> step composes. Absent on every
    /// other kind.
    /// </summary>
    /// <remarks>
    /// The child's steps are deliberately <em>not</em> in <see cref="Branches"/> — they are
    /// in that flow's own entry, which is where a change to them belongs. So a reader
    /// follows the name to another flow rather than descending into a copy of it, and one
    /// edit to a shared flow is one diff rather than one per flow that composes it.
    /// </remarks>
    [JsonPropertyName("flow")]
    public string? Flow { get; set; }

    /// <summary>
    /// How a <c>SubFlow</c> step relates to its child: <c>Inline</c> or <c>Detached</c>.
    /// Absent on every other kind.
    /// </summary>
    /// <remarks>
    /// Structure, not a value: it says whether the parent waits for the child and whether
    /// the child's failure is the parent's. That is the one thing about a composition worth
    /// reading off a diagram, which is why it is on the node's label.
    /// </remarks>
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    /// <summary>
    /// The identity an <c>AwaitSignal</c> step waits for. Absent on every other kind.
    /// </summary>
    /// <remarks>
    /// The one part of a step that is a <em>contract</em> rather than an implementation
    /// detail: it is the address a sender has to use to continue the flow, which is the same
    /// kind of fact as a trigger's route and is compared for the same reason
    /// (<c>FLOWX-DIFF-021</c> and <c>022</c>). Everything else about a step is refactoring.
    /// </remarks>
    [JsonPropertyName("signal")]
    public string? Signal { get; set; }

    /// <summary>
    /// The wait an <c>AwaitSignal</c> step declared, as an ISO-8601 duration.
    /// </summary>
    /// <remarks>
    /// Absent when the compiler could not evaluate the author's expression, which is a fact
    /// about the build rather than about the declaration — so <c>FLOWX-DIFF-206</c> renders a
    /// change into or out of that state as <c>(none)</c> and stays Neutral either way.
    /// </remarks>
    [JsonPropertyName("timeout")]
    public string? Timeout { get; set; }

    /// <summary>
    /// Nested blocks of a branching step: for a <c>Condition</c>, the <c>then</c> block
    /// first and the <c>Otherwise</c> block second when there is one; for a
    /// <c>Switch</c>, one block per case in declaration order and then the <c>Default</c>,
    /// which is always present and may be empty; for a <c>Parallel</c>, one block per
    /// branch in declaration order, every one of which runs.
    /// </summary>
    /// <remarks>
    /// Positional, because that is what the schema gives — <c>branches</c> is an array of
    /// arrays with nothing naming them. A one-element array on a <c>Condition</c>
    /// therefore means a <c>When</c> with no alternative, and an empty last array on a
    /// <c>Switch</c> means a value matching nothing falls through. The reader has to know
    /// that; the alternative would be a schema change nobody has agreed to.
    /// </remarks>
    [JsonPropertyName("branches")]
    public List<List<ManifestStep>> Branches { get; set; } = [];
}

/// <summary>One capability.</summary>
public sealed class ManifestCapability
{
    /// <summary>Business identity.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>Contract version.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>Fully-qualified CLR type name of the input contract.</summary>
    [JsonPropertyName("input")]
    public string? Input { get; set; }

    /// <summary>Fully-qualified CLR type name of the output contract.</summary>
    [JsonPropertyName("output")]
    public string? Output { get; set; }

    /// <summary>Whether a retry is declared safe.</summary>
    [JsonPropertyName("idempotent")]
    public bool Idempotent { get; set; }

    /// <summary>Named external effects.</summary>
    [JsonPropertyName("sideEffects")]
    public List<string> SideEffects { get; set; } = [];

    /// <summary>
    /// Every failure this capability can return, or <c>null</c> when the compiler could
    /// not resolve the catalogue.
    /// </summary>
    /// <remarks>
    /// <strong>Nullable on purpose, and it must stay that way.</strong> The generator emits
    /// three distinct states: a resolved catalogue, a resolved-and-empty one (<c>[]</c> —
    /// "this capability declares no errors"), and <em>no property at all</em> when the
    /// catalogue could not be read — a factory in a referenced assembly is enough. Defaulting
    /// this to an empty list collapsed the third state into the second, so a capability whose
    /// catalogue merely became unreadable was diffed as though every one of its codes had
    /// been deleted: <c>FLOWX-DIFF-017</c>, Breaking, in a gate that blocks the merge.
    /// A tool that turns a loss of information into a reported contract break is worse than
    /// one that says nothing.
    /// </remarks>
    [JsonPropertyName("errors")]
    public List<ManifestError>? Errors { get; set; }

    /// <summary>Replacement identity and removal date, when the contract is on its way out.</summary>
    [JsonPropertyName("deprecated")]
    public string? Deprecated { get; set; }

    /// <summary>Authorisation stance.</summary>
    [JsonPropertyName("authorization")]
    public ManifestAuthorization? Authorization { get; set; }
}

/// <summary>One declared failure of a capability.</summary>
public sealed class ManifestError
{
    /// <summary>Stable error code, e.g. <c>payment.declined</c>.</summary>
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    /// <summary>Validation, NotFound, Conflict, Forbidden, Unavailable or Internal.</summary>
    [JsonPropertyName("category")]
    public string? Category { get; set; }
}

/// <summary>One event type.</summary>
public sealed class ManifestEvent
{
    /// <summary>Business identity of the event.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>SemVer of the event's payload contract.</summary>
    [JsonPropertyName("schemaVersion")]
    public string? SchemaVersion { get; set; }

    /// <summary>The flows in this application that emit the event.</summary>
    /// <remarks>
    /// Total for the assembly that publishes the manifest, which is what makes it diffable:
    /// the compiler walks every <c>Emit</c> step it compiles, so a producer missing from this
    /// list is a producer that is not there. The neighbouring <c>consumedBy</c> is the
    /// opposite shape — estate-wide, unknowable from one compilation — and stays unwritten.
    /// </remarks>
    [JsonPropertyName("producedBy")]
    public List<string> ProducedBy { get; set; } = [];
}

/// <summary>A capability's authorisation stance.</summary>
public sealed class ManifestAuthorization
{
    /// <summary>Public, Authenticated, Permission, Policy or Internal.</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    /// <summary>The named permission or policy, when the mode needs one.</summary>
    [JsonPropertyName("value")]
    public string? Value { get; set; }
}

/// <summary>Source-generated serialisation, so the tool starts fast and publishes AOT.</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ManifestDocument))]
public sealed partial class ManifestJsonContext : JsonSerializerContext;
