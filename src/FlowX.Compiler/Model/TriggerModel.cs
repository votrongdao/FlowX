using System;
using System.Collections.Generic;
using System.Linq;

namespace FlowX.Compiler.Model;

/// <summary>
/// One declared way of starting a flow, expressed without a single Roslyn type
/// (<a href="../../../docs/adr/ADR-0004-universal-trigger-model.md">ADR-0004</a>).
/// </summary>
/// <remarks>
/// <para>
/// A trigger's identity is its <em>address</em> — the thing outside the process that a
/// caller, a broker or a scheduler uses to reach the flow. Everything on this model is
/// address or admission: the route a request arrives on, the topic a record is read
/// from, the expression a schedule fires on, whether an idempotency key is demanded
/// before the flow is created.
/// </para>
/// <para>
/// <strong>Operational tuning is deliberately absent.</strong> A Kafka trigger's
/// <c>MaxInFlight</c>, a cron trigger's <c>Jitter</c> and a stream trigger's
/// <c>Checkpoint</c> are all declared on the same attributes, and none of them are here.
/// They tune how the platform runs the trigger, not what the trigger promises anyone
/// outside it; publishing them would put deployment configuration into a contract
/// document and give <c>flowx diff</c> a whole class of changes to report that no
/// consumer can act on. The manifest schema's <c>trigger</c> object draws the same line,
/// and this model is exactly what fits it.
/// </para>
/// </remarks>
public sealed class TriggerModel : IEquatable<TriggerModel>
{
    /// <summary>Creates a model of one declared trigger.</summary>
    /// <param name="kind">The transport family: <c>Http</c>, <c>Bus</c>, <c>Schedule</c>, <c>Stream</c> or <c>Agent</c>.</param>
    /// <param name="method">HTTP method, for an <c>Http</c> trigger.</param>
    /// <param name="route">Route template, for an <c>Http</c> trigger.</param>
    /// <param name="idempotent">Whether an idempotency key is demanded at admission.</param>
    /// <param name="transport">Broker family, when the attribute names one.</param>
    /// <param name="topic">Topic, queue or stream source.</param>
    /// <param name="group">Consumer group.</param>
    /// <param name="cron">Cron expression, for a <c>Schedule</c> trigger.</param>
    /// <param name="timeZone">IANA time zone the cron expression is evaluated in.</param>
    /// <param name="description">Tool description shown to a model, for an <c>Agent</c> trigger.</param>
    /// <param name="confirmation">Human confirmation requirement, for an <c>Agent</c> trigger.</param>
    /// <param name="decoder">
    /// The fully qualified type translating this transport's payload into the flow's input, or
    /// <c>null</c> when the flow takes the payload itself. Never published: it names an
    /// implementation type, which is neither an address nor a term of the contract.
    /// </param>
    public TriggerModel(
        string kind,
        string? method = null,
        string? route = null,
        bool? idempotent = null,
        string? transport = null,
        string? topic = null,
        string? group = null,
        string? cron = null,
        string? timeZone = null,
        string? description = null,
        string? confirmation = null,
        string? decoder = null)
    {
        Kind = kind;
        Method = method;
        Route = route;
        Idempotent = idempotent;
        Transport = transport;
        Topic = topic;
        Group = group;
        Cron = cron;
        TimeZone = timeZone;
        Description = description;
        Confirmation = confirmation;
        Decoder = decoder;
    }

    /// <summary>The transport family, as the schema's <c>kind</c> enum spells it.</summary>
    public string Kind { get; }

    /// <summary>
    /// The type that turns this transport's payload into the flow's input, or <c>null</c>.
    /// </summary>
    /// <remarks>
    /// <strong>Read, and deliberately never written to the manifest.</strong> It is the name of
    /// an implementation type in this assembly — not an address a caller uses and not a term of
    /// the published contract — so publishing it would put a private name into a document
    /// consumers pin, and give <c>flowx diff</c> a change nobody outside the build can act on.
    /// That is the same line <c>MaxInFlight</c> and <c>DeadLetter</c> sit on the far side of
    /// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0039-a-bus-subscription-publishes-no-new-manifest-field.md">ADR-0039</a>).
    /// </remarks>
    public string? Decoder { get; }

    /// <summary>HTTP method, or <c>null</c>.</summary>
    public string? Method { get; }

    /// <summary>Route template, or <c>null</c>.</summary>
    public string? Route { get; }

    /// <summary>
    /// Whether the transport demands an idempotency key before the flow is created, or
    /// <c>null</c> when the trigger kind has no such notion.
    /// </summary>
    public bool? Idempotent { get; }

    /// <summary>Broker family, e.g. <c>kafka</c>, or <c>null</c> when the attribute does not name one.</summary>
    public string? Transport { get; }

    /// <summary>Topic, queue or stream source, or <c>null</c>.</summary>
    public string? Topic { get; }

    /// <summary>Consumer group, or <c>null</c>.</summary>
    public string? Group { get; }

    /// <summary>Cron expression, or <c>null</c>.</summary>
    public string? Cron { get; }

    /// <summary>IANA time zone id, or <c>null</c>.</summary>
    public string? TimeZone { get; }

    /// <summary>Agent tool description, or <c>null</c>.</summary>
    public string? Description { get; }

    /// <summary>Confirmation mode name, or <c>null</c>.</summary>
    public string? Confirmation { get; }

    /// <summary>
    /// Ordinal key the manifest sorts triggers by, so the document is byte-stable.
    /// </summary>
    /// <remarks>
    /// Roslyn returns a type's attributes in no promised order, and a flow may carry
    /// several triggers. Sorting on the address rather than on discovery order is what
    /// keeps two builds of identical source byte-identical.
    /// </remarks>
    public string SortKey => string.Join(
        "\0",
        new[]
        {
            Kind, Method, Route, Transport, Topic, Group, Cron, TimeZone, Description, Confirmation,
            Idempotent?.ToString(),

            // Included although it is never published, because this key is also this model's
            // equality — and the incremental generator caches on equality. Two triggers alike
            // but for their decoder would otherwise compare equal, and swapping a decoder
            // would leave the previous build's registration in place.
            Decoder,
        }.Select(part => part ?? string.Empty));

    /// <inheritdoc />
    public bool Equals(TriggerModel? other) =>
        other is not null && string.Equals(SortKey, other.SortKey, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as TriggerModel);

    /// <inheritdoc />
    public override int GetHashCode() => SortKey.GetHashCode();
}

/// <summary>
/// The part of a <c>[CronTrigger]</c> the manifest deliberately does not publish.
/// </summary>
/// <param name="Cron">
/// The expression, which is <em>not</em> carried for the registration to use — that comes off
/// the <see cref="TriggerModel"/> the manifest published — but as the key that joins this
/// declaration back to it. A flow may declare several schedules.
/// </param>
/// <param name="MissedFire">The declared <c>MissedFirePolicy</c> member, by name.</param>
/// <param name="PerTenant">
/// Whether the declaration asked for one firing per tenant, and therefore a fan-out over the
/// host's <c>ITenantDirectory</c> rather than a single instance per occurrence.
/// </param>
/// <param name="Overlap">The declared <c>OverlapPolicy</c> member, by name.</param>
/// <param name="Jitter">
/// The declared spread, verbatim — an ISO-8601 duration, or null when the declaration carried
/// none. Carried verbatim including a value the host will refuse, for
/// <see cref="StreamDeclaration"/>'s reason: discarding it here would leave <c>FLOWX1045</c>
/// nothing to name and would make the generated registration silently disagree with the source.
/// </param>
/// <remarks>
/// Separate from <see cref="TriggerModel"/> because that model is address and admission only,
/// and none of these four is either: they decide what this deployment does about work that is
/// late, whose work it is, what happens when it runs long and when in the minute it starts —
/// run-time behaviour rather than a promise to a caller
/// (<a href="../../../docs/adr/ADR-0034-the-manifest-publishes-a-schedules-address.md">ADR-0029</a>).
/// Folding them onto <see cref="TriggerModel"/> would have put values in the model whose own
/// remarks say operational tuning is deliberately absent, one field away from
/// <c>ManifestWriter</c> writing them.
/// </remarks>
public sealed record ScheduleDeclaration(
    string Cron,
    string MissedFire,
    bool PerTenant = false,
    string Overlap = "Skip",
    string? Jitter = null);

/// <summary>
/// The part of a <c>[StreamTrigger]</c> the manifest deliberately does not publish.
/// </summary>
/// <param name="Source">
/// The stream, which is <em>not</em> carried for the registration to use — that comes off the
/// <see cref="TriggerModel"/> the manifest published — but as the key that joins this declaration
/// back to it. A flow may declare several streams.
/// </param>
/// <param name="Window">The declared window, e.g. <c>tumbling:1m</c>.</param>
/// <param name="Lateness">The declared lateness, an ISO-8601 duration.</param>
/// <param name="Checkpoint">The declared checkpoint interval, an ISO-8601 duration.</param>
/// <param name="Parallelism">How many closed windows may have flows running at once.</param>
/// <remarks>
/// <see cref="ScheduleDeclaration"/>'s arrangement and its reason.
/// <see cref="TriggerModel"/>'s own remarks name "a stream trigger's <c>Checkpoint</c>" as the
/// example of operational tuning that is deliberately absent from the manifest — so these four
/// reach the generated registration without reaching the published contract, and the schema's
/// <c>trigger</c> object is unchanged.
/// </remarks>
public sealed record StreamDeclaration(
    string Source, string Window, string Lateness, string Checkpoint, int Parallelism);

/// <summary>Every trigger one flow declares, keyed by the flow's business identity.</summary>
/// <remarks>
/// <para>
/// Carried alongside <see cref="FlowModel"/> rather than on it, because a trigger is
/// read from an attribute on the flow's class while everything on <see cref="FlowModel"/>
/// is read from the <c>Define</c> chain. Keeping the two apart means the manifest can
/// gain declared triggers without the plan emitter — which has no use for them — being
/// touched at all.
/// </para>
/// <para>
/// Structural equality is implemented by hand so the incremental generator can cache
/// this value: a model compared by reference invalidates the manifest on every keystroke.
/// </para>
/// </remarks>
public sealed class FlowTriggersModel : IEquatable<FlowTriggersModel>
{
    /// <summary>Creates the trigger set for one flow.</summary>
    /// <param name="flowId">Business identity from <c>[Flow]</c>.</param>
    /// <param name="triggers">The triggers it declares, in any order.</param>
    /// <param name="schedules">
    /// The unpublished half of each <c>[CronTrigger]</c>, in declaration order. Empty for a flow
    /// that declares no schedule, which is most of them.
    /// </param>
    /// <param name="streams">
    /// The unpublished half of each <c>[StreamTrigger]</c>, in declaration order.
    /// </param>
    public FlowTriggersModel(
        string flowId,
        IReadOnlyList<TriggerModel> triggers,
        IReadOnlyList<ScheduleDeclaration>? schedules = null,
        IReadOnlyList<StreamDeclaration>? streams = null)
    {
        FlowId = flowId;
        Triggers = triggers
            .OrderBy(t => t.SortKey, StringComparer.Ordinal)
            .ToList();
        Schedules = schedules ?? Array.Empty<ScheduleDeclaration>();
        Streams = streams ?? Array.Empty<StreamDeclaration>();
    }

    /// <summary>Business identity of the flow these triggers start.</summary>
    public string FlowId { get; }

    /// <summary>The declared triggers, ordinally sorted.</summary>
    public IReadOnlyList<TriggerModel> Triggers { get; }

    /// <summary>What each <c>[CronTrigger]</c> declares that the manifest does not carry.</summary>
    public IReadOnlyList<ScheduleDeclaration> Schedules { get; }

    /// <summary>What each <c>[StreamTrigger]</c> declares that the manifest does not carry.</summary>
    public IReadOnlyList<StreamDeclaration> Streams { get; }

    /// <inheritdoc />
    public bool Equals(FlowTriggersModel? other) =>
        other is not null
        && string.Equals(FlowId, other.FlowId, StringComparison.Ordinal)
        && Triggers.Count == other.Triggers.Count
        && Triggers.SequenceEqual(other.Triggers)
        && Schedules.Count == other.Schedules.Count
        && Schedules.SequenceEqual(other.Schedules)
        && Streams.Count == other.Streams.Count
        && Streams.SequenceEqual(other.Streams);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as FlowTriggersModel);

    /// <inheritdoc />
    public override int GetHashCode() => FlowId.GetHashCode();
}
