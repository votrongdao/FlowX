namespace FlowX;

/// <summary>
/// Turns what a transport delivers into what a flow declares as its input.
/// </summary>
/// <typeparam name="TPayload">
/// What the transport hands over, and it is never the author's choice: a bus delivery gives
/// <see cref="BusMessage"/>, a schedule gives <see cref="ScheduledFire"/>, a closed stream
/// window gives its batch.
/// </typeparam>
/// <typeparam name="TInput">The flow's own input contract.</typeparam>
/// <remarks>
/// <para>
/// <strong>The problem this exists to remove.</strong> A trigger that delivers a payload fixes
/// what the flow's input type must be, and a class has one input type — so a flow reachable
/// over a bus, a schedule and HTTP had to be written three times, differing only in a first
/// step that translated the payload. That is what <c>samples/event-driven</c> shipped: four
/// classes whose step bodies are identical below the first one. Naming a decoder on the
/// attribute moves that first step out of the flow, and the four become one.
/// </para>
/// <para>
/// <strong>It is a translation, and it is not a capability.</strong> A capability runs inside
/// the step loop, under a policy stage and an authorisation stance, with a
/// <c>CapabilityContext</c> the engine seeded. A decode happens <em>before</em> the context
/// exists — the whole point is to produce the value the context is seeded with — so it gets no
/// stance, no policy and no telemetry. That is the right answer rather than a shortcut: a
/// decode has no side effect to police and nothing to authorise. Anything that does belongs in
/// a capability, one step in, where the engine can see it.
/// </para>
/// <para>
/// <strong>Synchronous, and deliberately.</strong> A decoder that awaited would be reaching
/// something — a schema registry, a database, another service — and a trigger path that can
/// block before the journal has opened is one that can lose a delivery while it waits. Parsing
/// what the transport already handed over needs no I/O.
/// </para>
/// <para>
/// <strong>A failure is a refusal, not an exception</strong>
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0007-result-over-exceptions.md">ADR-0007</a>).
/// A message whose body does not parse is a poison message, and the consumer's job is to
/// dead-letter it with a reason an operator can read — which it can only do if the decoder
/// returns one instead of throwing.
/// </para>
/// </remarks>
public interface ITriggerDecoder<in TPayload, TInput>
    where TPayload : notnull
    where TInput : notnull
{
    /// <summary>Translates one delivery into the flow's input.</summary>
    /// <param name="payload">What the transport delivered.</param>
    /// <returns>
    /// The flow's input, or an <see cref="Error"/> naming why this delivery cannot start one.
    /// </returns>
    Result<TInput> Decode(TPayload payload);
}
