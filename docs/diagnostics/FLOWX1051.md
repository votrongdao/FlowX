# FLOWX1051 — Trigger decoder does not produce the flow's input

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0
> **Applies to:** a trigger whose `Decode` names a type that does not implement
> `ITriggerDecoder<TPayload, TIn>` for that trigger's payload and that flow's input.

> [!NOTE]
> **This rule is the price of [FLOWX1039](FLOWX1039.md) and its three siblings standing down.**
> A trigger that carries a body normally forces the flow to declare that body as its input.
> Naming a decoder lifts that requirement — and it may only lift it if the decoder actually
> bridges the gap. This rule is the check that it does.

## What it means

`Decode` answers one question: *what turns what this transport delivers into what this flow
takes?* It is an answer only when the named type produces the flow's own input contract from
that transport's payload:

| Trigger | `TPayload` the decoder must accept |
|---|---|
| `[BusTrigger]`, `[KafkaTrigger]`, `[RabbitMqTrigger]`, `[ServiceBusTrigger]`, `[ChangeTrigger]` | `FlowX.BusMessage` |
| `[CronTrigger]` | `FlowX.ScheduledFire` |
| `[StreamTrigger]` | `FlowX.StreamWindowBatch` |

`TInput` must be the flow's own `TIn` — the first type argument of its `Flow<TIn, TOut>` base.

## Example that triggers it

```csharp
public sealed class ReadInvoiceRequest : ITriggerDecoder<BusMessage, IssueInvoice> { … }

// Reported: ReserveInventory is a capability, not a decoder for this flow's input.
[BusTrigger("invoice.requested", Group = "billing", Decode = typeof(ReserveInventory))]
public sealed partial class IssueInvoiceFlow : Flow<IssueInvoice, Invoice>
```

## How to fix it

Either name a type that decodes into the flow's input:

```csharp
[BusTrigger("invoice.requested", Group = "billing", Decode = typeof(ReadInvoiceRequest))]
public sealed partial class IssueInvoiceFlow : Flow<IssueInvoice, Invoice>
```

or drop `Decode` and declare the flow over the payload, which is what a flow that genuinely
wants the delivery should do — one that reads headers, or routes on the event type:

```csharp
[BusTrigger("invoice.requested", Group = "billing")]
public sealed partial class IssueInvoiceFlow : Flow<BusMessage, Invoice>
```

## Why it is an error and not a warning

There is no deployment, configuration or later release under which a value of the wrong type
becomes the flow's input. The generator has nothing to emit for a decoder it cannot call, so
what a suppression buys is a subscription registered against a flow no delivery can start —
and the manifest goes on publishing a trigger nothing serves.

## Why it is reported on the attribute

The named type is often a perfectly good type doing a perfectly good job somewhere else. What
is wrong is this declaration pointing at it, so that is where the squiggle goes. Reporting on
the decoder would send an author to a file with nothing to fix.

## Why it replaces the four rules rather than joining them

An author who has written `Decode` has already answered the question
[FLOWX1038](FLOWX1038.md), [FLOWX1039](FLOWX1039.md), [FLOWX1041](FLOWX1041.md) and
[FLOWX1042](FLOWX1042.md) ask. Reporting one of those beside this one would tell them to
declare the payload as the flow's input — which is to undo the decoder they were adding. The
same reasoning is [FLOWX1048](FLOWX1048.md)'s, one step further along.

## See also

- [FLOWX1048](FLOWX1048.md) — two triggers needing different contracts, with no decoder to
  reconcile them
- [FLOWX1039](FLOWX1039.md) — the rule a decoder stands in for on the bus path
