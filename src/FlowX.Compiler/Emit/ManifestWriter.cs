using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using FlowX.Compiler.Model;

namespace FlowX.Compiler.Emit;

/// <summary>
/// Writes <c>flowx.manifest.json</c> — the machine-readable description of the whole
/// application, and the artifact everything else is derived from
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0005-manifest-as-build-artifact.md">ADR-0005</a>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Deterministic by construction.</strong> Two builds of identical source must
/// produce byte-identical output, or <c>flowx diff</c> reports changes nobody made and
/// people stop reading it. That rules out a build timestamp and a commit hash — both
/// belong in the manifest, but injected at publish time by the CLI, not baked in by
/// the generator. Everything else is sorted ordinally rather than left in discovery
/// order, because Roslyn does not promise a stable order across builds.
/// </para>
/// <para>
/// <strong>Structure only, never values.</strong> The manifest says a capability
/// accepts a <c>CaptureRequest</c>; it never says what was in one. That rule is what
/// makes the file safe to publish to a marketplace, feed to an agent, or attach to a
/// build, and <c>ManifestContainsNoSecrets</c> asserts it.
/// </para>
/// <para>
/// Hand-written JSON rather than a serialiser: this assembly is a Roslyn analyzer, and
/// an analyzer that drags <c>System.Text.Json</c> along has to ship it too, which is a
/// well-known way to break other people's builds. The shape is small and fixed.
/// </para>
/// </remarks>
public static class ManifestWriter
{
    /// <summary>The schema version this writer emits. Bumped when the shape changes.</summary>
    public const string SchemaVersion = "0.1.0";

    /// <summary>
    /// The version stamped on every published event, in the manifest and on the outbox row.
    /// </summary>
    /// <remarks>
    /// A constant, and named rather than repeated because it is now written in two places
    /// that must agree: the manifest's <c>events</c> array, which is what a consumer team
    /// reads, and <c>OutboxWrite.SchemaVersion</c>, which is what arrives beside the body. Two
    /// literals would be a drift nobody notices until a consumer versions off the wrong one.
    /// <para>
    /// It is a constant rather than a declaration because nothing declares one yet — there is
    /// no attribute on an event contract to read it from. That is ADR-0018's revisit, and
    /// when it lands both writers change together because they read this.
    /// </para>
    /// </remarks>
    public const string EventSchemaVersion = "1.0.0";

    /// <summary>Writes the manifest for a whole application.</summary>
    /// <param name="applicationName">Usually the root assembly name.</param>
    /// <param name="applicationVersion">SemVer of the application.</param>
    /// <param name="flows">Every flow in the compilation.</param>
    /// <param name="projectDirectory">
    /// Absolute path of the project being compiled. Source pointers are written relative
    /// to it, so the document is identical on every machine that builds the same source.
    /// </param>
    /// <param name="triggers">
    /// The triggers each flow declares, read from its attributes. Flows with no entry
    /// publish no <c>triggers</c> array — see <see cref="WriteTriggers"/>.
    /// </param>
    /// <param name="errorCatalogues">
    /// The failures each capability can return, keyed by <c>id@version</c>. A catalogue
    /// that could not be established completely is not published — see
    /// <see cref="WriteErrors"/>.
    /// </param>
    public static string Write(
        string applicationName,
        string applicationVersion,
        IReadOnlyList<FlowModel> flows,
        string? projectDirectory = null,
        IReadOnlyList<FlowTriggersModel>? triggers = null,
        IReadOnlyList<CapabilityErrorCatalogue>? errorCatalogues = null)
    {
        if (flows is null)
        {
            throw new System.ArgumentNullException(nameof(flows));
        }

        var ordered = flows.OrderBy(f => f.FlowId, System.StringComparer.Ordinal).ToList();
        var triggersByFlow = Index(triggers, t => t.FlowId);
        var errorsByCapability = Index(errorCatalogues, c => c.Key);
        var writer = new JsonWriter();

        writer.OpenObject();
        writer.Property("schemaVersion", SchemaVersion);

        writer.PropertyName("application");
        writer.OpenObject();
        writer.Property("name", applicationName);
        writer.Property("version", applicationVersion);
        writer.CloseObject();

        writer.PropertyName("flows");
        writer.OpenArray();
        foreach (var flow in ordered)
        {
            WriteFlow(writer, flow, projectDirectory, triggersByFlow, errorsByCapability);
        }

        writer.CloseArray();

        writer.PropertyName("capabilities");
        writer.OpenArray();
        foreach (var capability in CollectCapabilities(ordered))
        {
            WriteCapability(writer, capability, errorsByCapability);
        }

        writer.CloseArray();

        writer.PropertyName("events");
        writer.OpenArray();

        var producers = CollectProducers(ordered);

        foreach (var evt in CollectEvents(ordered))
        {
            writer.OpenObject();
            writer.Property("type", evt);
            writer.Property("schemaVersion", EventSchemaVersion);
            WriteProducers(writer, producers, evt);
            writer.CloseObject();
        }

        writer.CloseArray();
        writer.CloseObject();

        return writer.ToString();
    }

    /// <summary>
    /// Turns an absolute <c>file:line</c> into one relative to the project directory.
    /// </summary>
    /// <remarks>
    /// The manifest is a published artifact that <c>flowx diff</c> compares across builds
    /// and machines, and the header on the generated holder promises it is byte-identical
    /// for identical source. An absolute path breaks that promise on the second machine,
    /// and ships the build agent's directory layout to anyone who reads the manifest.
    /// Falls back to the original when no project directory is known, because a slightly
    /// wrong pointer beats no pointer at all.
    /// </remarks>
    private static string Relativise(string location, string? projectDirectory)
    {
        if (string.IsNullOrEmpty(projectDirectory))
        {
            return location;
        }

        var prefix = projectDirectory!.Replace('\\', '/');

        if (!prefix.EndsWith("/", System.StringComparison.Ordinal))
        {
            prefix += "/";
        }

        var normalised = location.Replace('\\', '/');

        return normalised.StartsWith(prefix, System.StringComparison.Ordinal)
            ? normalised.Substring(prefix.Length)
            : normalised;
    }

    private static void WriteFlow(
        JsonWriter writer,
        FlowModel flow,
        string? projectDirectory,
        Dictionary<string, FlowTriggersModel> triggers,
        Dictionary<string, CapabilityErrorCatalogue> errors)
    {
        writer.OpenObject();
        writer.Property("id", flow.FlowId);
        writer.Property("version", flow.Version);
        writer.Property("profile", flow.Profile);

        if (flow.Deadline != null)
        {
            writer.Property("deadline", flow.Deadline);
        }

        writer.PropertyName("input");
        writer.OpenObject();
        writer.Property("type", flow.InputTypeName);
        WriteSensitive(writer, flow.SensitiveInputMembers);
        writer.CloseObject();

        writer.PropertyName("output");
        writer.OpenObject();
        writer.Property("type", flow.OutputTypeName);
        WriteSensitive(writer, flow.SensitiveOutputMembers);
        writer.CloseObject();

        WriteTriggers(writer, triggers.TryGetValue(flow.FlowId, out var declared) ? declared : null);

        writer.PropertyName("steps");
        writer.OpenArray();
        foreach (var step in flow.Steps)
        {
            WriteStep(writer, step);
        }

        writer.CloseArray();

        writer.PropertyName("emits");
        writer.OpenArray();
        foreach (var evt in flow.AllSteps
            .Where(s => s.Kind == StepKindModel.Emit && s.EventType != null)
            .Select(s => s.EventType!)
            .Distinct(System.StringComparer.Ordinal)
            .OrderBy(e => e, System.StringComparer.Ordinal))
        {
            writer.Value(evt);
        }

        writer.CloseArray();

        WriteFlowErrors(writer, flow, errors);

        if (flow.DeclarationLocation != null)
        {
            writer.Property("source", Relativise(flow.DeclarationLocation, projectDirectory));
        }

        writer.CloseObject();
    }

    /// <summary>
    /// Writes the flow's aggregate error codes — the union of its capabilities' — or
    /// nothing when any one of them is unknown.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The schema describes this field as "aggregated from its capabilities. Generated
    /// OpenAPI responses derive from it", and that is the whole of its meaning: it states
    /// nothing the capability entries do not already state, and it exists so a consumer
    /// building a response table for one endpoint does not have to walk the step graph to
    /// assemble it. <c>flowx diff</c> deliberately ignores it for the same reason —
    /// diffing derived data as well as its source reports every change twice.
    /// </para>
    /// <para>
    /// Codes only, without categories, because that is the shape the schema declares here.
    /// A consumer that needs the category joins on the capability entry, where the pair is
    /// kept together.
    /// </para>
    /// <para>
    /// One unresolved capability withholds the whole array. A union that is short by one
    /// capability's worth of codes reads exactly like a complete one, and an OpenAPI
    /// document generated from it would omit responses the endpoint really returns.
    /// </para>
    /// </remarks>
    private static void WriteFlowErrors(
        JsonWriter writer, FlowModel flow, Dictionary<string, CapabilityErrorCatalogue> errors)
    {
        var invoked = flow.AllSteps
            .SelectMany(Invoked)
            .Where(step => step.CapabilityId != null)
            .Select(step => step.CapabilityId + "@" + step.CapabilityVersion)
            .Distinct(System.StringComparer.Ordinal)
            .ToList();

        var catalogues = new List<CapabilityErrorCatalogue>();

        foreach (var key in invoked)
        {
            if (!errors.TryGetValue(key, out var catalogue) || !catalogue.IsComplete)
            {
                return;
            }

            catalogues.Add(catalogue);
        }

        if (catalogues.Count == 0)
        {
            return;
        }

        writer.PropertyName("errors");
        writer.OpenArray();

        foreach (var code in catalogues
            .SelectMany(c => c.Errors)
            .Select(e => e.Code)
            .Distinct(System.StringComparer.Ordinal)
            .OrderBy(code => code, System.StringComparer.Ordinal))
        {
            writer.Value(code);
        }

        writer.CloseArray();
    }

    /// <summary>
    /// Writes the flow's declared triggers, or nothing when it declares none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Omitted rather than emitted empty, and for the opposite reason to
    /// <c>errors</c>.</strong> An empty <c>triggers</c> array would read as "this flow
    /// cannot be started from outside the process", and the generator is not entitled to
    /// say that. Nothing yet turns a trigger attribute into a registration — the sample's
    /// route is mapped by hand in <c>Program.cs</c> — so a flow with no attribute may
    /// still be serving traffic. What the compiler knows is what was <em>declared</em>,
    /// and a positive statement about declared triggers is sound where the negative one
    /// is not.
    /// </para>
    /// <para>
    /// A trigger attribute this build does not recognise is skipped upstream in
    /// <c>TriggerReader</c> rather than guessed at, so a flow whose only trigger comes
    /// from a third-party transport plugin lands here with nothing to write. That is the
    /// same absence, with the same meaning: not "no triggers", but "none this compiler can
    /// read".
    /// </para>
    /// </remarks>
    private static void WriteTriggers(JsonWriter writer, FlowTriggersModel? triggers)
    {
        if (triggers is null || triggers.Triggers.Count == 0)
        {
            return;
        }

        writer.PropertyName("triggers");
        writer.OpenArray();

        foreach (var trigger in triggers.Triggers)
        {
            writer.OpenObject();
            writer.Property("kind", trigger.Kind);
            WriteOptional(writer, "method", trigger.Method);
            WriteOptional(writer, "route", trigger.Route);
            WriteOptional(writer, "transport", trigger.Transport);
            WriteOptional(writer, "topic", trigger.Topic);
            WriteOptional(writer, "group", trigger.Group);
            WriteOptional(writer, "cron", trigger.Cron);
            WriteOptional(writer, "timeZone", trigger.TimeZone);
            WriteOptional(writer, "description", trigger.Description);
            WriteOptional(writer, "confirmation", trigger.Confirmation);

            if (trigger.Idempotent.HasValue)
            {
                writer.Property("idempotent", trigger.Idempotent.Value);
            }

            writer.CloseObject();
        }

        writer.CloseArray();
    }

    private static void WriteOptional(JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            writer.Property(name, value!);
        }
    }

    /// <summary>Indexes a nullable collection by a key, tolerating duplicates.</summary>
    private static Dictionary<string, T> Index<T>(IReadOnlyList<T>? items, System.Func<T, string> key)
    {
        var indexed = new Dictionary<string, T>(System.StringComparer.Ordinal);

        foreach (var item in items ?? (IReadOnlyList<T>)System.Array.Empty<T>())
        {
            indexed[key(item)] = item;
        }

        return indexed;
    }

    /// <summary>Writes the contract's sensitive members, or nothing when it has none.</summary>
    /// <remarks>
    /// Omitted rather than emitted empty so the manifest of a contract with no secrets is
    /// unchanged by this field existing — an empty array in every flow would be noise in
    /// every <c>flowx diff</c>.
    /// </remarks>
    private static void WriteSensitive(JsonWriter writer, IReadOnlyList<string> members)
    {
        if (members.Count == 0)
        {
            return;
        }

        writer.PropertyName("sensitive");
        writer.OpenArray();

        foreach (var member in members)
        {
            writer.Value(member);
        }

        writer.CloseArray();
    }

    private static void WriteStep(JsonWriter writer, StepModel step)
    {
        writer.OpenObject();
        writer.Property("id", step.Index);
        writer.Property("kind", StepKindName(step.Kind));

        if (step.CapabilityId != null)
        {
            writer.Property("capability", step.CapabilityId + "@" + step.CapabilityVersion);
        }

        if (step.CompensationId != null)
        {
            writer.Property("compensation", step.CompensationId + "@" + step.CompensationVersion);
        }

        if (step.EventType != null)
        {
            writer.Property("event", step.EventType);
        }

        WriteSubFlow(writer, step);
        WriteWait(writer, step);
        WritePolicies(writer, step);
        WriteBranches(writer, step);

        writer.CloseObject();
    }

    /// <summary>
    /// Writes which flow a <c>SubFlow</c> step composes, and how.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Both fields are structure, and the distinction is the one that makes this
    /// file safe to publish.</strong> <c>flow</c> is the child's business identity — the
    /// same string the child's own manifest entry carries — not a value the flow computed;
    /// it is what turns a set of flow documents into a graph a reviewer, <c>flowx diff</c>
    /// or an agent can walk. <c>mode</c> says whether the parent waits for the child and
    /// whether the child's failure is the parent's, which is a fact about the shape of the
    /// composition and about nothing flowing through it.
    /// </para>
    /// <para>
    /// <strong>The input mapping is not published, for the reason a predicate is not.</strong>
    /// <c>ctx =&gt; new FulfilOrder(ctx.Get&lt;OrderId&gt;())</c> names the shape of
    /// somebody's data, and the rule that makes this document publishable is structure only,
    /// never values. A reader therefore sees that a flow composes another and on what terms,
    /// but not with what.
    /// </para>
    /// <para>
    /// <strong>The child's steps are not published here either.</strong> They are in the
    /// child's own entry, which is where a change to them belongs — splicing them in would
    /// make every parent's document grow with the child's and turn one edit to a shared
    /// flow into a diff in every flow that composes it.
    /// </para>
    /// </remarks>
    private static void WriteSubFlow(JsonWriter writer, StepModel step)
    {
        if (step.Kind != StepKindModel.SubFlow)
        {
            return;
        }

        if (step.SubFlowId != null)
        {
            writer.Property("flow", step.SubFlowId);
        }

        if (step.SubFlowMode != null)
        {
            writer.Property("mode", step.SubFlowMode);
        }
    }

    /// <summary>
    /// Writes what an <c>AwaitSignal</c> step waits for, and how long it declared to wait.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Both fields are structure, and the first is an address.</strong> Until
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0021-manifest-publishes-the-wait.md">ADR-0021</a>
    /// this step published <c>{ "id": n, "kind": "AwaitSignal" }</c> and nothing else, so two
    /// flows waiting for different things were byte-identical here and <c>flowx diff</c> had
    /// no field to compare. <c>signal</c> is the identity a transport addresses a delivery to
    /// — the same string <c>StepNode.SignalType</c> carries and the generated signal endpoint
    /// puts in its route — which makes it the same kind of fact as a trigger's route: how the
    /// flow is reached from outside, not what any instance carried.
    /// </para>
    /// <para>
    /// <strong><c>timeout</c> is the folded duration and never the author's expression.</strong>
    /// The plan carries <c>Waits.Countersignature</c> verbatim because generated C# can
    /// evaluate it; a consumer reading this document cannot, and a <c>FLOWX-DIFF-206</c> over a
    /// symbol name would fire on a rename and stay silent on a change of value. So the
    /// compiler evaluates what it can and this omits the rest — <see cref="WriteParallelBranches"/>'s
    /// stance on an unreadable merge strategy, for the same reason: an absent field is a
    /// consumer asking, a guessed one is a consumer misled.
    /// </para>
    /// <para>
    /// Nothing arms the wait — there is no scheduler and no timer table — so this publishes
    /// what was <em>declared</em>, exactly as <c>policies</c> does for a policy set nothing
    /// executes. It is not a value the writer invented, which is the line that matters.
    /// </para>
    /// </remarks>
    private static void WriteWait(JsonWriter writer, StepModel step)
    {
        // A poll declares a timeout in exactly the sense a suspension point does — how long
        // before the escalation runs — so it publishes the same field, folded the same way.
        // Its `interval` deliberately does not appear: the schema's step object publishes
        // structure, and how often a loop asks is the same kind of tuning number as
        // MaxDegreeOfParallelism, which has no field either. A reader learns that the flow
        // polls, what it polls, and how long it will keep trying.
        if (step.Kind == StepKindModel.Poll)
        {
            // And `signal` when the poll declared an `.OrSignal<T>()`, on the same grounds this
            // field is published for a suspension point: it is the identity a transport
            // addresses a delivery to, so it is how the flow is reached from outside rather than
            // anything an instance carried — which is also what makes it comparable, and puts a
            // poll's second ending under `flowx diff`'s existing wait rules with no rule of its
            // own. A poll with one ending publishes no field, which is the honest difference.
            WriteOptional(writer, "signal", step.SignalType);
            WriteOptional(writer, "timeout", step.PollTimeoutIso);
            return;
        }

        if (step.Kind != StepKindModel.AwaitSignal)
        {
            return;
        }

        WriteOptional(writer, "signal", step.SignalType);
        WriteOptional(writer, "timeout", step.SignalTimeoutIso);
    }

    /// <summary>
    /// Writes a conditional's blocks as the schema's <c>branches</c>: an array of arrays
    /// of steps, <c>then</c> first and <c>Otherwise</c> second.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Nested, while the compiled plan is flat.</strong> The manifest publishes
    /// the shape the author declared, not the layout the engine executes — a reader
    /// comparing the manifest against a <c>Define</c> method should recognise it. So the
    /// branch-and-jump form stays inside the generated C#, and the jump in particular has
    /// no manifest entry at all: it exists only because the plan is one array, and
    /// publishing it would invite a consumer to draw an edge that is not part of the
    /// design.
    /// </para>
    /// <para>
    /// Step ids stay the compiled flat indices, so a manifest step and a plan step still
    /// name the same thing. The consequence is that ids are not contiguous across a
    /// conditional — the jump's index is missing — and a consumer that assumed
    /// contiguity would be wrong. Renumbering to close the gap would be worse: the ids
    /// would stop matching the indices in traces, in <c>flowx replay</c> and in the
    /// generated dispatcher.
    /// </para>
    /// <para>
    /// <strong>The predicate is not published, and neither is a switch's selector or its
    /// case values.</strong> The schema's step object is <c>additionalProperties: false</c>
    /// and has no field for either, so there is nowhere to put them without changing the
    /// committed contract — and there should not be one. <c>Channel.Wholesale</c> is a
    /// business value, and the rule that makes this file safe to publish is structure
    /// only, never values. A reader therefore sees that a flow branches and where each
    /// branch goes, but not on what.
    /// </para>
    /// </remarks>
    private static void WriteBranches(JsonWriter writer, StepModel step)
    {
        if (step.Kind == StepKindModel.Switch)
        {
            WriteSwitchBranches(writer, step);
            return;
        }

        if (step.Kind == StepKindModel.Parallel)
        {
            WriteParallelBranches(writer, step);
            return;
        }

        if (step.Kind == StepKindModel.ForEach)
        {
            WriteIterationBody(writer, step);
            return;
        }

        // An `.OnTimeout(...)` block is carried in Then, the way a conditional's block is,
        // so it writes as a `branches` array with exactly one entry. Without this the block's
        // steps reached no branches array at all: their capabilities still appeared in the
        // manifest's top-level capability list, because FlowModel.AllSteps walks SelfAndNested,
        // so the manifest named work it could not place — a reader saw an escalation's
        // capability with nothing in the step tree that runs it. One entry rather than two,
        // because a wait has no `Otherwise`: the other way out is the rest of the flow.
        if (step.Kind == StepKindModel.AwaitSignal)
        {
            if (step.Then.Count == 0)
            {
                return;
            }

            writer.PropertyName("branches");
            writer.OpenArray();
            WriteBranch(writer, step.Then);
            writer.CloseArray();
            return;
        }

        // A poll writes two entries where a wait writes one: the attempt it repeats, then the
        // escalation it takes when the budget runs out. Both are blocks the flow may run and
        // both would otherwise be capabilities named in the top-level list with nothing in the
        // step tree that runs them — the untruth the AwaitSignal arm above was added to end.
        // The attempt is always present; the escalation may be absent, and then there is one
        // entry, which is the shape a reader should see for a poll that simply fails.
        if (step.Kind == StepKindModel.Poll)
        {
            writer.PropertyName("branches");
            writer.OpenArray();
            WriteBranch(writer, step.Body);

            if (step.Then.Count > 0)
            {
                WriteBranch(writer, step.Then);
            }

            writer.CloseArray();
            return;
        }

        if (step.Kind != StepKindModel.Condition)
        {
            return;
        }

        writer.PropertyName("branches");
        writer.OpenArray();

        WriteBranch(writer, step.Then);

        if (step.Otherwise.Count > 0)
        {
            WriteBranch(writer, step.Otherwise);
        }

        writer.CloseArray();
    }

    /// <summary>
    /// Writes a switch's blocks as <c>branches</c>: one array per case in declaration
    /// order, then the <c>Default</c> block when there is one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every case gets an entry, empty block or not, because the position in this array is
    /// the only thing that identifies which arm a block belongs to once the values are
    /// gone. Dropping the empty ones would silently renumber the rest.
    /// </para>
    /// <para>
    /// The default block is always written too, even when the flow declares none, so the
    /// last entry is unambiguously the default and a consumer can tell a switch that
    /// handles a miss from one that falls through. That is the opposite choice from a
    /// conditional's absent <c>Otherwise</c>, which is simply omitted — but a conditional
    /// has at most two blocks, so the omission is unambiguous there and would not be here.
    /// </para>
    /// </remarks>
    private static void WriteSwitchBranches(JsonWriter writer, StepModel step)
    {
        writer.PropertyName("branches");
        writer.OpenArray();

        foreach (var arm in step.Cases)
        {
            WriteBranch(writer, arm.Steps);
        }

        WriteBranch(writer, step.Default);

        writer.CloseArray();
    }

    /// <summary>
    /// Writes a fork's blocks as <c>branches</c>, and the merge rule as <c>merge</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same <c>branches</c> array a conditional and a switch use, because it means the
    /// same thing: these blocks belong to this step. What differs is only how many of them
    /// run, and <c>kind</c> already says that.
    /// </para>
    /// <para>
    /// <strong><c>merge</c> is structure, so it belongs here — and a quorum's size does
    /// not.</strong> "This fork waits for all its branches" describes the flow's shape, the
    /// same way <c>compensation</c> and <c>policies</c> do; it tells a consumer what
    /// failure of one branch means without telling them anything about the data. The
    /// <em>number</em> in <c>Quorum(n)</c> comes from an arbitrary expression on the
    /// author's side, and publishing an evaluated constant would start the manifest down
    /// the road of carrying values. So the label is published and the argument is not.
    /// </para>
    /// <para>
    /// Omitted entirely when the strategy could not be read statically. An absent field is
    /// a consumer asking; a guessed one is a consumer misled.
    /// </para>
    /// </remarks>
    private static void WriteParallelBranches(JsonWriter writer, StepModel step)
    {
        if (step.MergeKindName != null)
        {
            writer.Property("merge", step.MergeKindName);
        }

        writer.PropertyName("branches");
        writer.OpenArray();

        foreach (var branch in step.Branches)
        {
            WriteBranch(writer, branch.Steps);
        }

        writer.CloseArray();
    }

    /// <summary>
    /// Writes a loop's body as <c>branches</c>: one array, because a loop has one block.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same <c>branches</c> array the other three shapes use, because it means the same
    /// thing: these steps belong to this step. What differs is how many times the block
    /// runs, and <c>kind</c> already says <c>ForEach</c>.
    /// </para>
    /// <para>
    /// <strong>Neither the collection nor the bound is published, and the two are refused
    /// for different reasons.</strong> The selector is a business value in the sense that
    /// matters — <c>ctx.Get&lt;ValidatedOrder&gt;().Lines</c> names the shape of somebody's
    /// data — and the rule that makes this file safe to publish is structure only, never
    /// values. <c>MaxDegreeOfParallelism</c> is not a value in that sense, it is a bound;
    /// it stays out because the committed schema's step object is
    /// <c>additionalProperties: false</c> and has no field for it, and a tuning number is
    /// not worth changing a published contract for. A reader therefore sees that a flow
    /// iterates and what it does per element, but not over what and not how fast.
    /// </para>
    /// </remarks>
    private static void WriteIterationBody(JsonWriter writer, StepModel step)
    {
        writer.PropertyName("branches");
        writer.OpenArray();
        WriteBranch(writer, step.Body);
        writer.CloseArray();
    }

    private static void WriteBranch(JsonWriter writer, IReadOnlyList<StepModel> block)
    {
        writer.OpenArray();

        foreach (var step in block)
        {
            WriteStep(writer, step);
        }

        writer.CloseArray();
    }

    /// <summary>
    /// The stage each policy kind runs in, fixed by ADR-0011.
    /// </summary>
    /// <remarks>
    /// Duplicated from <c>PolicySet</c> rather than referenced, because this assembly
    /// targets netstandard2.0 and cannot link against <c>FlowX.Abstractions</c> — the
    /// same constraint that makes the model layer its own thing. The duplication is
    /// pinned by <c>PolicyStagesMatchTheAbstraction</c>, which reads the real mapping by
    /// reflection and fails if these two ever disagree. An unpinned copy of a safety
    /// ordering is exactly the kind of duplication that drifts silently.
    /// </remarks>
    private static readonly System.Collections.Generic.Dictionary<string, string> PolicyStages =
        new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.Ordinal)
        {
            ["RateLimit"] = "Admission",
            ["Idempotency"] = "Integrity",
            ["Timeout"] = "Resilience",
            ["Retry"] = "Resilience",
            ["CircuitBreaker"] = "Resilience",
            ["Bulkhead"] = "Resilience",
            ["Cache"] = "Efficiency",
            ["Audit"] = "Consistency",

            // WP-57. Consistency rather than Resilience, because it wraps the step's
            // *compensation* and Consistency is where ADR-0011 puts compensation: the unwind
            // is that stage's obligation discharged later, and the retry is a parameter of it.
            ["CompensationRetry"] = "Consistency",
        };

    /// <summary>Every policy kind this writer knows a stage for.</summary>
    /// <remarks>Exposed so the fitness test can compare it with the real abstraction.</remarks>
    public static IReadOnlyDictionary<string, string> KnownPolicyStages => PolicyStages;

    /// <summary>
    /// Writes the step's policies, or nothing when it declares none.
    /// </summary>
    /// <remarks>
    /// A policy whose stage this writer does not recognise is skipped rather than
    /// guessed at: the stage is the safety property, and a wrong one in the manifest
    /// would misrepresent the order things run in.
    /// </remarks>
    private static void WritePolicies(JsonWriter writer, StepModel step)
    {
        var known = step.PolicyKinds.Where(PolicyStages.ContainsKey).ToList();

        if (known.Count == 0)
        {
            return;
        }

        writer.PropertyName("policies");
        writer.OpenArray();

        foreach (var kind in known)
        {
            writer.OpenObject();
            writer.Property("kind", kind);
            writer.Property("stage", PolicyStages[kind]);
            writer.CloseObject();
        }

        writer.CloseArray();
    }

    private static void WriteCapability(
        JsonWriter writer, StepModel step, Dictionary<string, CapabilityErrorCatalogue> errors)
    {
        writer.OpenObject();
        writer.Property("id", step.CapabilityId!);
        writer.Property("version", step.CapabilityVersion!);
        writer.Property("input", step.CapabilityInput ?? "object");
        writer.Property("output", step.CapabilityOutput ?? "object");

        writer.PropertyName("authorization");
        writer.OpenObject();
        writer.Property("mode", step.AuthorizationMode ?? "Public");

        // `value` is the name the stance checks against, and its field name and shape are
        // the schema's, not this writer's — `capability.authorization.value`, a string.
        // Omitted rather than emitted empty, because the schema makes it optional and
        // three of the five modes name nothing: an empty string would read as "a
        // permission whose name is blank" instead of "this stance needs no name".
        //
        // Until this line existed, FLOWX-DIFF-015's second half — "the named permission
        // changed" — compared null against null on every manifest FlowX produced and
        // could not fire. The compiler had the value the whole time and dropped it here.
        if (step.AuthorizationValue is { Length: > 0 } value)
        {
            writer.Property("value", value);
        }

        writer.CloseObject();

        writer.Property("idempotent", step.IsIdempotent);

        writer.PropertyName("sideEffects");
        writer.OpenArray();
        foreach (var effect in step.SideEffects.OrderBy(e => e, System.StringComparer.Ordinal))
        {
            writer.Value(effect);
        }

        writer.CloseArray();

        errors.TryGetValue(step.CapabilityId + "@" + step.CapabilityVersion, out var catalogue);
        WriteErrors(writer, catalogue);

        writer.CloseObject();
    }

    /// <summary>
    /// Writes the capability's error catalogue, or nothing when it is not known to be
    /// complete.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>An empty array is emitted, and it means something.</strong> This is the
    /// opposite of <see cref="WriteSensitive"/>, and the difference is the point of the
    /// field. A contract either carries <c>[Sensitive]</c> members or it does not, and the
    /// compiler always knows which — so omitting an empty <c>sensitive</c> array loses
    /// nothing. An error catalogue has a third state: the reader followed the failure
    /// paths and could not resolve one of them. Three states need three renderings, and
    /// collapsing "returns nothing" into "could not tell" is exactly the ambiguity this
    /// field exists to remove — a consumer reading <c>errors: []</c> is entitled to
    /// conclude that this capability never fails with a declared code, and would be wrong
    /// if the array were also what an unreadable capability produced.
    /// </para>
    /// <para>
    /// Codes and categories only. The message is left behind deliberately: it is the field
    /// that interpolates business values, and the manifest is publishable to consumers not
    /// entitled to them.
    /// </para>
    /// </remarks>
    private static void WriteErrors(JsonWriter writer, CapabilityErrorCatalogue? catalogue)
    {
        if (catalogue is null || !catalogue.IsComplete)
        {
            return;
        }

        writer.PropertyName("errors");
        writer.OpenArray();

        foreach (var error in catalogue.Errors)
        {
            writer.OpenObject();
            writer.Property("code", error.Code);
            writer.Property("category", error.Category);
            writer.CloseObject();
        }

        writer.CloseArray();
    }

    /// <summary>
    /// Every distinct capability across every flow, deduplicated by <c>id@version</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deduplication is by identity and version, not by CLR type: two flows invoking
    /// the same capability must produce one manifest entry, and the same capability at
    /// two contract versions must produce two.
    /// </para>
    /// <para>
    /// <strong>Compensations count.</strong> They were previously listed only as a name on
    /// the step they undo, so their authorisation stance, side effects and idempotency
    /// never reached the manifest and <c>flowx diff</c> could not see a breaking change to
    /// one. A compensation is a capability that happens to run backwards; the manifest
    /// says so.
    /// </para>
    /// </remarks>
    private static IEnumerable<StepModel> CollectCapabilities(IEnumerable<FlowModel> flows)
    {
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        var capabilities = new List<StepModel>();

        foreach (var step in flows.SelectMany(f => f.AllSteps).SelectMany(Invoked))
        {
            if (step.CapabilityId != null && seen.Add(step.CapabilityId + "@" + step.CapabilityVersion))
            {
                capabilities.Add(step);
            }
        }

        return capabilities.OrderBy(
            c => c.CapabilityId + "@" + c.CapabilityVersion,
            System.StringComparer.Ordinal);
    }

    /// <summary>The capabilities one step invokes: itself, and its compensation if any.</summary>
    private static IEnumerable<StepModel> Invoked(StepModel step)
    {
        if (step.Kind == StepKindModel.Capability)
        {
            yield return step;
        }

        if (step.Compensation is not null)
        {
            yield return step.Compensation;
        }
    }

    /// <summary>Maps each event to the flows that emit it, in one pass over the steps.</summary>
    /// <remarks>
    /// <para>
    /// <strong>The fact was already walked; only the writing was missing.</strong>
    /// <see cref="CollectEvents"/> reaches every <c>Emit</c> step of every flow to build the
    /// catalogue and then discards which flow each one came from. Publishing it closes one of
    /// the rows
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0017-manifest-v1-freeze-criteria.md">ADR-0017</a>
    /// tables as declared-but-never-written — and a field the schema declares and nothing
    /// writes is worse than a missing field, because absence reads as "this application has
    /// none" rather than as "nobody looked".
    /// </para>
    /// <para>
    /// <strong>One pass, because the obvious spelling is quadratic.</strong> Asking
    /// "which flows emit <em>this</em> event" per event walks every step of every flow once
    /// per event, and a synthetic 50-flow project measured that at +3.3 % of the generator's
    /// whole allocation budget — past the +2 % the relative gate blocks at
    /// (<c>docs/benchmarks/generator-cost-gate.md</c>). Inverting it to one dictionary built
    /// in a single pass costs the same walk <c>CollectEvents</c> already makes.
    /// </para>
    /// <para>
    /// <strong>Producers, not consumers.</strong> The neighbouring <c>consumedBy</c> stays
    /// empty and that is not an oversight: a subscriber is an estate-wide fact and one
    /// compilation can only see its own, so writing it here would publish a list that is
    /// complete for this service and silently partial for the question anybody asks it
    /// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0039-a-bus-subscription-publishes-no-new-manifest-field.md">ADR-0039</a>).
    /// A producer is the opposite: an event is emitted by the flows of the assembly that
    /// publishes the manifest, so this list is total by construction.
    /// </para>
    /// </remarks>
    private static Dictionary<string, SortedSet<string>> CollectProducers(IEnumerable<FlowModel> flows)
    {
        var producers = new Dictionary<string, SortedSet<string>>(System.StringComparer.Ordinal);

        foreach (var flow in flows)
        {
            foreach (var step in flow.AllSteps)
            {
                if (step.Kind != StepKindModel.Emit || step.EventType == null)
                {
                    continue;
                }

                if (!producers.TryGetValue(step.EventType, out var emitters))
                {
                    emitters = new SortedSet<string>(System.StringComparer.Ordinal);
                    producers.Add(step.EventType, emitters);
                }

                emitters.Add(flow.FlowId);
            }
        }

        return producers;
    }

    private static void WriteProducers(
        JsonWriter writer, Dictionary<string, SortedSet<string>> producers, string eventType)
    {
        if (!producers.TryGetValue(eventType, out var emitters) || emitters.Count == 0)
        {
            return;
        }

        writer.PropertyName("producedBy");
        writer.OpenArray();

        foreach (var producer in emitters)
        {
            writer.Value(producer);
        }

        writer.CloseArray();
    }

    private static IEnumerable<string> CollectEvents(IEnumerable<FlowModel> flows) => flows
        .SelectMany(f => f.AllSteps)
        .Where(s => s.Kind == StepKindModel.Emit && s.EventType != null)
        .Select(s => s.EventType!)
        .Distinct(System.StringComparer.Ordinal)
        .OrderBy(e => e, System.StringComparer.Ordinal);

    private static string StepKindName(StepKindModel kind) => kind switch
    {
        StepKindModel.Emit => "Emit",
        StepKindModel.AwaitSignal => "AwaitSignal",
        StepKindModel.Condition => "Condition",
        StepKindModel.Switch => "Switch",
        StepKindModel.Parallel => "Parallel",
        StepKindModel.ForEach => "ForEach",
        StepKindModel.SubFlow => "SubFlow",

        // The kind, and deliberately nothing else. The committed schema's step object is
        // additionalProperties:false and has no field for an error — and the Error an
        // author writes carries a message that routinely interpolates business values,
        // which is the same line CapabilityErrorModel draws when it publishes a code and a
        // category and never a message. "This arm terminates the flow" is structure; what
        // it terminates with stays in compiled code.
        StepKindModel.Fail => "Fail",

        // A timer is a step that takes a while, and the schema has listed "Delay" in the
        // step `kind` enum since before anything emitted one. Without this arm the default
        // below published a delay as {"id": 3, "kind": "Capability"} with no `capability`
        // field — schema-valid, and false: a consumer diffing two manifests would see a
        // capability step appear and disappear as a `.Delay(...)` moved. The duration is
        // deliberately absent, on `merge`'s precedent and `Fail`'s: the manifest carries
        // structure, and a delay's duration is an arbitrary expression in the flow's source.
        StepKindModel.Delay => "Delay",

        // A poll is its own kind rather than a Capability with an interval on it, because what
        // it says about the flow is a fact about control: this step runs many times and may
        // escalate. Publishing the attempt as an ordinary capability step and the loop as
        // nothing would have made a flow that polls indistinguishable from one that calls once.
        StepKindModel.Poll => "Poll",

        _ => "Capability",
    };

    /// <summary>Minimal, deterministic JSON emitter. Two-space indent, ordinal escaping.</summary>
    private sealed class JsonWriter
    {
        private readonly StringBuilder _builder = new StringBuilder();
        private int _indent;
        private bool _needsComma;

        public void OpenObject() => Open('{');

        public void CloseObject() => Close('}');

        public void OpenArray() => Open('[');

        public void CloseArray() => Close(']');

        public void PropertyName(string name)
        {
            Separate();
            Indent();
            _builder.Append(Quote(name)).Append(": ");
            _needsComma = false;
        }

        public void Property(string name, string value)
        {
            PropertyName(name);
            _builder.Append(Quote(value));
            _needsComma = true;
        }

        public void Property(string name, int value)
        {
            PropertyName(name);
            _builder.Append(value.ToString(CultureInfo.InvariantCulture));
            _needsComma = true;
        }

        public void Property(string name, bool value)
        {
            PropertyName(name);
            _builder.Append(value ? "true" : "false");
            _needsComma = true;
        }

        public void Value(string value)
        {
            Separate();
            Indent();
            _builder.Append(Quote(value));
            _needsComma = true;
        }

        public override string ToString() => _builder.Append('\n').ToString();

        private void Open(char brace)
        {
            // A property name has already written its own separator and indent.
            if (_needsComma || _builder.Length == 0 || _builder[_builder.Length - 1] != ' ')
            {
                Separate();
                Indent();
            }

            _builder.Append(brace);
            _indent++;
            _needsComma = false;
        }

        private void Close(char brace)
        {
            _indent--;

            if (_needsComma)
            {
                _builder.Append('\n');
                Indent();
            }

            _builder.Append(brace);
            _needsComma = true;
        }

        private void Separate()
        {
            if (_needsComma)
            {
                _builder.Append(',');
            }

            if (_builder.Length > 0)
            {
                _builder.Append('\n');
            }
        }

        private void Indent() => _builder.Append(' ', _indent * 2);

        private static string Quote(string value)
        {
            var builder = new StringBuilder("\"");

            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            return builder.Append('"').ToString();
        }
    }
}
