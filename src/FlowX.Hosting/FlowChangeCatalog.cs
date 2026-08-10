using FlowX.Runtime;

namespace FlowX.Hosting;

/// <summary>One change subscription this node serves, and the flow it starts.</summary>
/// <param name="Subscription">What it observes, and under whose group.</param>
/// <param name="Flow">The compiled plan and its dispatcher.</param>
public sealed record ChangeRegistration(ChangeSubscription Subscription, FlowRegistration Flow)
{
    /// <summary>The id the instance for one observed change is started under.</summary>
    /// <param name="changeId">The change's own identity.</param>
    public Guid InstanceIdFor(Guid changeId) => ChangeIdentity.InstanceIdFor(
        Subscription.FlowId,
        Subscription.FlowVersion,
        Subscription.Source,
        Subscription.Group,
        changeId);

    /// <summary>The lease that makes this node the only one reading this subscription.</summary>
    public Guid SubscriptionLease => ChangeIdentity.SubscriptionLeaseIdFor(
        Subscription.FlowId, Subscription.FlowVersion, Subscription.Source, Subscription.Group);
}

/// <summary>
/// Which change subscriptions this node serves, and the plans behind them.
/// </summary>
/// <remarks>
/// <see cref="FlowBusCatalog"/>'s shape and its reasons, including registration rather than
/// discovery: reflecting over loaded assemblies to find <c>[ChangeTrigger]</c> would be a
/// trim-time dependency on types nothing statically references, which constraint C2 forbids.
/// </remarks>
public sealed class FlowChangeCatalog
{
    private readonly Lock _gate = new();
    private readonly Dictionary<SubscriptionKey, ChangeRegistration> _subscriptions = [];

    /// <summary>How many change subscriptions this node serves.</summary>
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
    public IReadOnlyList<ChangeRegistration> Registrations
    {
        get
        {
            lock (_gate)
            {
                return [.. _subscriptions
                    .OrderBy(static entry => entry.Key.FlowId, StringComparer.Ordinal)
                    .ThenBy(static entry => entry.Key.FlowVersion, StringComparer.Ordinal)
                    .ThenBy(static entry => entry.Key.Source, StringComparer.Ordinal)
                    .ThenBy(static entry => entry.Key.Group, StringComparer.Ordinal)
                    .Select(static entry => entry.Value)];
            }
        }
    }

    /// <summary>Makes a change subscription observable on this node.</summary>
    /// <param name="subscription">What it observes, and under whose group.</param>
    /// <param name="plan">The compiled flow.</param>
    /// <param name="dispatcher">Invokes the capability behind each step index.</param>
    /// <returns>The same catalogue, so registrations chain.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// The plan is not the flow the subscription names, it does not declare
    /// <see cref="ExecutionProfile.Durable"/>, it emits the very type the subscription
    /// observes, or it closes a cycle with the subscriptions already registered.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <strong>An ephemeral flow is refused</strong>, for <see cref="FlowBusCatalog.Add(BusSubscription, ExecutionPlan, IStepDispatcher)"/>'s
    /// reason: nothing journals an ephemeral instance, so the id a change derives
    /// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0049-a-change-names-the-instance-it-starts.md">ADR-0049</a>)
    /// is inert and every re-read of an uncommitted cursor position runs the flow again with
    /// nothing recording that it had. <c>FLOWX1041</c> is the earlier half of the same rule.
    /// </para>
    /// <para>
    /// <strong>A flow that emits the type it observes is refused, and this check exists nowhere
    /// else</strong>
    /// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0050-a-change-trigger-observes-the-outbox.md">ADR-0047</a>
    /// decision 3). The staged event starts the flow, the flow stages another under a fresh
    /// identity, and the feed offers that one — for ever, with the outbox growing and every
    /// instance legitimately distinct, so nothing downstream can tell it from work. The emitted
    /// types are in the plan, which is why the answer is read from the artifact that will run
    /// rather than from syntax.
    /// </para>
    /// <para>
    /// <strong>An indirect cycle is refused by the same rule read over the whole
    /// catalogue.</strong> A observes <c>x</c> and emits <c>y</c>, B observes <c>y</c> and emits
    /// <c>x</c>: neither flow observes what it emits, and between them they run for ever.
    /// ADR-0050 decision 3 recorded that as unbounded because "the catalogue sees one
    /// registration at a time" — it does not; it holds every subscription registered before this
    /// one, and a subscription's source together with its plan's <c>Emit</c> set is an edge. The
    /// registration that would close the cycle is the one refused, which makes the refusal
    /// deterministic in registration order and names a real pair rather than a set.
    /// </para>
    /// <para>
    /// Last registration wins for a given <c>(id, version, source, group)</c>, for
    /// <see cref="FlowCatalog.Add"/>'s reason.
    /// </para>
    /// </remarks>
    public FlowChangeCatalog Add(
        ChangeSubscription subscription, ExecutionPlan plan, IStepDispatcher dispatcher)
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
                "instance id and the cursor are both keyed on, so a mismatch would start one " +
                "flow under another's changes and read another's position.",
                nameof(plan));
        }

        if (plan.Flow.Profile != ExecutionProfile.Durable)
        {
            throw new ArgumentException(
                $"Flow '{subscription.FlowId}' observes '{subscription.Source}' and declares the " +
                $"profile '{plan.Flow.Profile}'. A change-triggered flow must declare Durable: " +
                "nothing journals an ephemeral instance, so the id a change derives is inert, " +
                "there is no primary key to refuse a re-read, and one change would start one " +
                "flow per pass until the cursor happened to commit — with no error, no duplicate " +
                "row and nothing anywhere to count.",
                nameof(plan));
        }

        if (Emits(plan, subscription.Source))
        {
            throw new ArgumentException(
                $"Flow '{subscription.FlowId}' observes '{subscription.Source}' and emits it. " +
                "The change would start the flow, the flow would stage another event of that " +
                "type under a fresh identity, and the feed would offer that one — for ever, " +
                "with every instance legitimately distinct so nothing refuses it and nothing " +
                "downstream can tell the loop from work. Observe a different type, or emit one.",
                nameof(plan));
        }

        var key = new SubscriptionKey(
            subscription.FlowId,
            subscription.FlowVersion,
            subscription.Source,
            subscription.Group);

        lock (_gate)
        {
            if (CycleClosedBy(subscription, plan, key) is { Count: > 0 } cycle)
            {
                throw new ArgumentException(
                    $"Flow '{subscription.FlowId}' observes '{subscription.Source}' and closes a " +
                    $"cycle across this node's subscriptions: {Describe(cycle)}. Each flow in it " +
                    "emits what the next one observes, so a single change starts a run that " +
                    "stages the change that starts the next — for ever, with every instance " +
                    "legitimately distinct so nothing refuses it and nothing downstream can tell " +
                    "the loop from work. Break the chain: one of these flows has to observe a " +
                    "different type, or emit one.",
                    nameof(plan));
            }

            _subscriptions[key] =
                new ChangeRegistration(subscription, new FlowRegistration(plan, dispatcher));
        }

        return this;
    }

    /// <summary>
    /// The chain of subscriptions this registration would close into a loop, or null.
    /// </summary>
    /// <param name="subscription">What the incoming registration observes.</param>
    /// <param name="plan">Its plan, whose <c>Emit</c> nodes are its outgoing edges.</param>
    /// <param name="key">Its key, so a re-registration is measured against its replacement.</param>
    /// <returns>The hops, starting with this registration, or null when there is no cycle.</returns>
    /// <remarks>
    /// <para>
    /// <strong>Only the incoming edge is searched, because the rest of the graph is already
    /// acyclic.</strong> Every registration passes through here, so no cycle can exist among the
    /// subscriptions already held — which makes "does this close one" a walk forward from the
    /// types this plan emits, looking for a way back to the type it observes, rather than a
    /// cycle search over the whole graph on every registration.
    /// </para>
    /// <para>
    /// <strong>The key being replaced is excluded.</strong> Last registration wins, so the edge
    /// the incoming one is about to take the place of is not part of the graph it joins;
    /// counting it would refuse a flow for a cycle with the version of itself it supersedes.
    /// </para>
    /// <para>
    /// Called under <c>_gate</c> because the answer is about the whole dictionary and a
    /// concurrent <see cref="Add"/> would otherwise be able to insert the closing edge between
    /// this walk and the write below it.
    /// </para>
    /// </remarks>
    private List<Hop>? CycleClosedBy(
        ChangeSubscription subscription, ExecutionPlan plan, SubscriptionKey key)
    {
        if (!plan.HasEmit)
        {
            return null;
        }

        // Ordered so that a node registering the same subscriptions reports the same cycle:
        // an error message that varies with dictionary layout is an error message two operators
        // compare and disagree about.
        var observers = _subscriptions
            .Where(entry => entry.Key != key)
            .OrderBy(static entry => entry.Key.FlowId, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Key.FlowVersion, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Key.Source, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Key.Group, StringComparer.Ordinal)
            .Select(static entry => entry.Value)
            .ToLookup(static registration => registration.Subscription.Source, StringComparer.Ordinal);

        var path = new List<Hop>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var emitted in Emitted(plan))
        {
            path.Add(new Hop(subscription.FlowId, subscription.Source, emitted));

            if (Reaches(emitted, subscription.Source, observers, seen, path))
            {
                return path;
            }

            path.RemoveAt(path.Count - 1);
        }

        return null;
    }

    /// <summary>Whether an emitted type leads back to the type the new subscription observes.</summary>
    /// <param name="emitted">The type just staged by the hop at the end of <paramref name="path"/>.</param>
    /// <param name="target">The type the incoming subscription observes.</param>
    /// <param name="observers">Which registrations observe which type.</param>
    /// <param name="seen">Types already walked, so a shared prefix is not re-walked.</param>
    /// <param name="path">The hops so far; on true it is the cycle.</param>
    private static bool Reaches(
        string emitted,
        string target,
        ILookup<string, ChangeRegistration> observers,
        HashSet<string> seen,
        List<Hop> path)
    {
        if (string.Equals(emitted, target, StringComparison.Ordinal))
        {
            return true;
        }

        if (!seen.Add(emitted))
        {
            return false;
        }

        foreach (var observer in observers[emitted])
        {
            foreach (var next in Emitted(observer.Flow.Plan))
            {
                path.Add(new Hop(observer.Subscription.FlowId, emitted, next));

                if (Reaches(next, target, observers, seen, path))
                {
                    return true;
                }

                path.RemoveAt(path.Count - 1);
            }
        }

        return false;
    }

    /// <summary>The cycle as an operator has to read it: every flow in it, and both its types.</summary>
    private static string Describe(List<Hop> cycle) => string.Join(
        ", then ",
        cycle.Select(static hop =>
            $"'{hop.Flow}' observes '{hop.Observes}' and emits '{hop.Stages}'"));

    /// <summary>The distinct types a plan's <c>Emit</c> nodes stage, in step order.</summary>
    private static IEnumerable<string> Emitted(ExecutionPlan plan) =>
        plan.HasEmit
            ? plan.Graph.Steps
                .Where(static step => step.Kind == StepKind.Emit && step.EventType is not null)
                .Select(static step => step.EventType!)
                .Distinct(StringComparer.Ordinal)
            : [];

    /// <summary>Whether any <c>Emit</c> node in the plan stages the type given.</summary>
    /// <remarks>
    /// <see cref="ExecutionPlan.HasEmit"/> is checked first because it is a bool the plan already
    /// computed, and a plan that emits nothing is the common case.
    /// </remarks>
    private static bool Emits(ExecutionPlan plan, string type) =>
        plan.HasEmit &&
        plan.Graph.Steps.Any(step =>
            step.Kind == StepKind.Emit &&
            string.Equals(step.EventType, type, StringComparison.Ordinal));

    /// <summary>Ordinal by construction: an id, a version, a type and a group are identifiers.</summary>
    private readonly record struct SubscriptionKey(
        string FlowId, string FlowVersion, string Source, string Group);

    /// <summary>One flow in a cycle: what it observes, and the type it stages in response.</summary>
    private readonly record struct Hop(string Flow, string Observes, string Stages);
}
