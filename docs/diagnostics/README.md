# Diagnostics

Every `FLOWX####` the compiler can report, with what it means, an example that
triggers it, and the fix. The `helpLinkUri` on each descriptor points here, and
`EveryDiagnosticIsHelpful` fails the build if a descriptor lacks one.

## Why these are errors, not warnings

Each rule below encodes something that is either structurally impossible to recover
from at run time, or expensive enough that discovering it in production is the wrong
place. A warning is a rule nobody has to obey; if a rule is worth having, it stops
the build.

Thirteen entries below are not errors, or not only errors, and each says why on its own page. Three of the thirteen —
the determinism set [FLOWX1007](FLOWX1007.md), [FLOWX1008](FLOWX1008.md) and
[FLOWX1009](FLOWX1009.md) — say why *together*, in
[the section below](#the-severity-of-the-determinism-set), because
[06 §5](../06-Execution-Engine.md#5-the-determinism-boundary) asked for that stance to be
taken as a set rather than one row at a time.
[FLOWX1024](FLOWX1024.md) and [FLOWX1025](FLOWX1025.md) report gaps between the
manifest and what the build can actually deliver, rather than mistakes in the source
— and `FLOWX1025` is additionally about an attribute the developer sometimes does not
own, which an error would make unusable. It follows `FLOWX1011`'s asymmetry rather
than being lenient everywhere: an **error** when the trigger attribute is declared in
the compilation being built, where the fix is one line the reader owns, and a
**warning** when it arrives from a referenced package, where it is not their omission
to fix. [FLOWX1028](FLOWX1028.md) is the same shape
taken to its limit — the declared execution profile is one the runtime does not
implement at all — and an error there would be actively harmful, because its only
repair is to delete the declaration P2 will need to find. [FLOWX1027](FLOWX1027.md) reports code that
has no effect rather than code that is wrong, which is exactly what C#'s own
`CS0162` is and exactly the severity C# gives it. [FLOWX1011](FLOWX1011.md) is an error in `Durable`
flows and a warning in `Ephemeral` ones, which is the asymmetry
[ADR-0003](../adr/ADR-0003-execution-profiles.md) ratified for the determinism rules:
a durable flow is replayed and must take the branch it took the first time, an
ephemeral one is not replayed at all.
[FLOWX1012](FLOWX1012.md) is the one rule here whose remedy has a prerequisite outside the
source file, which is most of why it is not an error;
[the section below](#the-severity-of-flowx1012-which-is-not-the-determinism-sets-argument)
is its argument, kept apart from the determinism set's on purpose.
`FLOWX1031` was the twelfth and **is deleted**, with the gap it described;
[the section below](#flowx1031-is-deleted-with-what-it-described) is what it said and why the
argument for keeping a rule of that shape expired.
`FLOWX1032` was the thirteenth and **is deleted** the same way, with the last of the gap
*it* described; [its section](#flowx1032-is-deleted-with-what-it-described) is what it said,
where its reasoning was `FLOWX1028`'s one level down, and why narrowing it a third time was
not an option.

## The severity of the determinism set

[ADR-0003](../adr/ADR-0003-execution-profiles.md) says the determinism rules are **errors
under `Durable` and informational under `Ephemeral`**.
[06 §5](../06-Execution-Engine.md#5-the-determinism-boundary) repeats it, and then asks for
the stance to be revisited **as a set** once the journal exists — "`FLOWX1011`'s deviation
included, rather than one row at a time". WP-52 gave the runtime a journal and WP-58 raised
`FLOWX1007`–`FLOWX1009`. This is that revisit, and this section is its record.

**The set:** `FLOWX1006`, `FLOWX1007`, `FLOWX1008`, `FLOWX1009`, `FLOWX1011`. **`FLOWX1006`
joined it in WP-59**, and applying the rule below to it yields an **Error uniformly** rather
than a split: the rule reports only on a `Durable` flow, so its trigger *is* the escalation
condition and there is no case left to warn about. That is `FLOWX1012`'s mutual exclusivity
read from the other end — that rule fires *because* a flow is not durable and so can never
escalate; this one fires *because* it is and so can never fail to.

### The decision

> **Warning by default. Error where the compilation can prove the code is on a durable
> flow's replay path. Never informational.**

For a flow, "can prove" is its own `Profile`. For a capability — which has no profile, and
is reached from flows that may not be in this compilation at all — it means a `Durable` flow
in this compilation names it as a step, directly or through a sub-flow it composes.

### Why not `Info`, which is what the ADR says

Because an `Info` diagnostic never appears in a build log. `dotnet build` does not print it,
MSBuild does not fail on it, and no gate in [21-Quality-Gates](../21-Quality-Gates.md)
notices it. `Ephemeral` is the default profile and the one nearly every flow uses, so a set
of rules that is informational there is a set of rules that does nothing in nearly every
build — the precise "documented but unenforced" state this catalogue was written to end, and
the state all four of these ids were already in.

This is not a new argument. `FLOWX1011` deviated from the ADR for exactly this reason and
recorded the deviation as provisional; `FLOWX1028` rejected `Info` on the same grounds and
said so on its page. Taking the set together, the honest conclusion is that the deviation was
right and the ADR's `Info` was wrong: **`Warning` is now the rule and `FLOWX1011` is no
longer an exception to anything.** Nothing about `FLOWX1011`'s behaviour changes; what
changes is that it stops being a deviation. ADR-0003's bullet and 06 §5's paragraph are
superseded on this point and should be amended to match.

### Why `Warning` is not the lenient option

This repository sets `TreatWarningsAsErrors`, so every rule in the set stops **this** build.
What the warning buys is not leniency, it is the right unit of decision for a consumer: one
line in `.editorconfig`, written down in the repository that took the decision, instead of a
`#pragma` per capability or a rule that cannot be adopted incrementally at all. That is the
trade [FLOWX1016](FLOWX1016.md) and [FLOWX1028](FLOWX1028.md) already make.

### Why the escalation is a proof and not a guess

[FLOWX1011](FLOWX1011.md) escalates on the flow's declared profile;
[FLOWX1025](FLOWX1025.md) escalates when the offending attribute is declared in the
compilation being built. Both choose severity by **who can act on the finding**, and both
escalate only on something the compiler can see. The determinism set follows them.

The asymmetry is deliberate. Failing to escalate — a capability whose durable caller lives in
a referenced assembly — costs a warning instead of an error, and under
`TreatWarningsAsErrors` that is still a stopped build. Escalating wrongly would stop someone
else's build on a claim the compiler cannot support, and a rule that does that is a rule
suppressed everywhere, which protects nothing. So the reach analysis is transitive through
sub-flows, because a sub-flow runs inside its parent's instance and the escalation would
otherwise be one composition away from being avoidable — and it stops at the edge of the
compilation, because that is where the evidence stops.

### What the escalation is *not* claiming

That an ephemeral violation is acceptable. `FLOWX1009` is the clearest case: a mutable field
on a capability is a **race between concurrent invocations** under either profile, because
capabilities are resolved once and invoked concurrently. Its ephemeral warning is a statement
about who can act on it and how urgently, not about whether the code is correct.

### Why this is now worth doing at all

Because a durable flow genuinely journals. Until WP-52 the runtime read `ExecutionProfile`
nowhere, so `Durable` ran the ephemeral engine and a determinism violation in a "durable"
flow was a defect in a replay that could not happen. It now commits one journal row per step
boundary, captures `ctx.UtcNow`, `ctx.NewId()` and `ctx.Random`'s seed per step, and resumes
by replaying committed rows into the same step loop. A violation in such a flow is a real
replay defect. *This paragraph then said, so as not to overclaim, that "nothing replays the
capture back into execution yet" and that until `ReplayDeterminismTest` existed a determinism
leak left no trace at run time.* **WP-61 (2026-07-31) built it** — `ReplayDeterminismTests`,
a corpus of eight shapes each replayed against its own journal — so a leak in a shape the
corpus covers does now leave a trace. That is not an argument against a build-time rule: a
corpus catches a leak in the flows somebody wrote a corpus entry for, and these rules catch
it in every flow that compiles, before anything runs.

## The severity of `FLOWX1012`, which is not the determinism set's argument

`FLOWX1012` is *about* a profile, arrived through the same ADR bullet, and was unraised for
the same two phases — so the tempting move is to fold it into the set above and inherit its
answer. That would be wrong in the one place it matters, and the difference is worth stating
here rather than only on the page.

**The set's rule is "Warning by default, Error where the compilation can prove the code is on
a durable flow's replay path". `FLOWX1012` fires *because* the flow is not durable.** Its
trigger condition and the set's escalation condition are mutually exclusive: there is no
compilation in which this rule reports and that proof is available. The escalation does not
transfer, and inventing a different one would be inventing a rule, not applying a stance.

There is exactly one escalation a reader will propose — a `Durable` parent composing this
flow as a sub-flow, by the same transitive reasoning the determinism set uses — and it is
the one case the runtime does **not** currently honour. A resumed parent skips a completed
sub-flow's row and deliberately does not rebuild that child's compensation stack: the entry
is bound to a context that died with the node, and rebuilding it from the child's own rows
is WP-57. Escalating there would promise a guarantee the engine does not deliver, on the
strength of a profile that does not reach the thing being escalated about.

**Why not an error, then.** Three reasons, and only the third is about adoption:

1. **The source is not wrong.** A compensable `Ephemeral` flow compensates correctly on
   every ordinary failure — the capture declines, the unwind runs, the reservation comes
   back. What it loses is the crash window. [ADR-0003](../adr/ADR-0003-execution-profiles.md)
   records that trade deliberately, and `docs/DEBT.md` names it as the example of a
   *decision* rather than debt. An error would make a decision the ADR ratified
   inexpressible.
2. **The remedy has a prerequisite the compiler cannot see.** `Profile = Durable` needs a
   host with a journal and a lease store registered; without one, `FlowInvocation` refuses
   the flow with `flow.durability_not_configured` before its first step. An error would stop
   a build until the author made an edit whose correctness depends on a deployment fact no
   analyzer can check. That is also why this rule ships with **no code fix** — see the page.
3. **`Ephemeral` is the default profile**, so an error here breaks every compensable flow in
   every codebase that has not already opted into durability, on the day it is switched on.

**Why not `Info`, for the same reason as the set, only more so.** Info never reaches a build
log; and this rule reports *only* on flows that did not opt into durability, which is the
overwhelming majority. An Info `FLOWX1012` would be invisible in essentially every build
that could ever contain it — which is precisely the state it was already in for two phases,
and the state raising it was supposed to end.

**`Warning` is not the lenient option here either.** This repository sets
`TreatWarningsAsErrors`, and the rule's first finding was `samples/ecommerce` — the
reference application, a compensable saga on `Ephemeral`. It stopped that build, which is
the evidence that the rule reports on real code rather than on a fixture. What the sample
did about it is on [the page](FLOWX1012.md#the-reference-sample-fires-this-rule).

## `FLOWX1031` is deleted, with what it described

**Deleted on 2026-08-02, when the timer half of WP-63 landed.** The rule reported that
`.Delay(...)` and `.OnTimeout(...)` compiled to nothing: the call reached `FlowAnalyzer`'s
`default:` arm and was skipped, so a delay occupied no index and an escalation block reached
no plan, no dispatcher and no manifest. Both are now laid out and both run. A rule that
outlives the gap it describes is noise, and noise is what teaches people to suppress a
catalogue — so the descriptor, its analysis, its page and its tests went together, and the id
is retired rather than reused.

**This section is what it said, kept because a catalogue that quietly rewrites its own
history teaches nobody what it got wrong.**

**One rule, three constructs, two severities.** The line was between a construct the compiler
omitted and one it falsified. `Delay` and `OnTimeout` produced no step, so the generated plan
said less than the source and nothing in it was untrue — the category
[FLOWX1027](FLOWX1027.md) occupies, at the severity C# gives `CS0162`. `AwaitSignal` produced
a step that reached the plan and the dispatcher carrying `TimeSpan.FromHours(1)`, a value no
author wrote in place of one they did, because the compiler's step model had no field for a
timeout and `StepNode.ForAwaitSignal` demands one. That left two options — emit a plan
containing a duration nobody wrote, or emit no plan — and a warning would have been a rule
that reports a falsification in the build log and then commits it in the generated source. So
that half was an **error**.

**WP-63 took the third option the model had made unavailable, in two steps.** First
`StepModel.SignalTimeout` carried the author's expression into the plan and `FlowEngine`
gained a suspension point, which retired the error half and narrowed the rule to two
constructs. Then `StepModel.Delay`, `StepKind.Delay` and the escalation block retired the
rest. The refusal in `FlowEmitter` is still there and still says the same thing — this
generator does not invent a duration — and now covers both kinds; what changed is that
nothing reaches it, because neither DSL method has an overload without one.

**Why it was narrowed rather than deleted at the first step**, and why that reasoning stopped
applying at the second: [FLOWX1028](FLOWX1028.md) was narrowed to `Streaming` when `Durable`
started running rather than deleted, because deleting it would have handed `Streaming` the
silence `Durable` had. The same argument held for a discarded `OnTimeout` block while it was
still discarded. It does not hold for a construct that works.

**And the cost the split used to carry is gone.** With `FLOWX1017` an error below `Durable`
and this an error at it, `AwaitSignal` had no profile it could legally declare, and
`AwaitSignalRequiresDurableCodeFixProvider` was a quick action whose result was a different
diagnostic — the thing `ExecutionProfileAnalyzerTests` calls a broken fix. The premise the
provider's own remarks rest on, *"the author wrote `AwaitSignal`, so the flow suspends"*, is
true now, so its output is a flow that compiles and waits.

## `FLOWX1032` is deleted, with what it described

**Deleted on 2026-08-02, when stage 5 and stage 7's `Audit` landed beside the stage 1 and
stage 3 that had landed alongside them.** The rule reported that a declared policy reached the
plan and the manifest and no code applied it. It was narrowed twice rather than deleted — from
the eight kinds it was written over to four when the policy engine landed
`PolicyStage.Resilience`, then to two when `RateLimit` and `Idempotency` started executing —
and there is no third narrowing, because every kind `PolicySet` offers is now read by
`StepPolicy.From`, `StepAudit.From` or `CompensationPolicy.From`. A rule that outlives the gap
it describes is noise, and noise is what teaches people to suppress a catalogue — so the
descriptor, its analysis, its page, its release row, its tests and `samples/banking`'s
`#pragma warning disable` went together, and the id is retired rather than reused. That is
`FLOWX1028`'s and `FLOWX1031`'s precedent: a build log or a suppression naming `FLOWX1032`
means what it said when it was written, and reusing the number would silently retarget it.

**What survives it is `DeclaredPolicyAnalyzer.ExecutedKinds`**, which was the list the rule
was the complement of. The complement is empty and the list is not: it is the compiler's
statement of what the runtime applies, pinned against `StepPolicy`'s constants,
`StepAudit.AuditKind` and `CompensationPolicy.CompensationRetryKind` by
`PolicyStageFitnessTests`, and it is what will notice the next kind `PolicySet` gains before
any resolver reads it.

**This section is what it said, kept because a catalogue that quietly rewrites its own
history teaches nobody what it got wrong.**

A **warning**, and the argument was not new: it was [FLOWX1028](FLOWX1028.md)'s, moved from
the flow's `Profile` to a step's `.WithPolicy(...)`. Both of that rule's halves transfer,
which is worth saying explicitly, because the deleted `FLOWX1031` — the nearest neighbour in
shape — could only use one of them.

**An error erases the inventory the fixing phase needs.** The only edit that silences an
error is deleting the `.WithPolicy(...)` call or emptying the set. That declaration is the
list of the steps that asked for a rate limit or a cache, and it is the same greppability
ADR-0003 lists as a positive consequence for the profile. **The argument has been paid off
once already**: when the policy engine landed stage 4, the flows that got a working timeout
and a working retry were exactly the flows whose declarations this rule had refused to make
them delete.

**The source is not wrong.** This is the half `FLOWX1031` could not use, and it is what puts
this rule on FLOWX1028's side of the line. *No flow is correct with a seven-day wait
compiled to no wait* — but a great many flows are correct with a `RateLimit` enforced by the
gateway in front of the process, or an `Idempotency` window an idempotent endpoint already
provides. "Confirm the flow is correct as it is, and record that" is a real remedy here and
is the page's first one.

**And nothing is falsified.** `FLOWX1031`'s error half turned on the plan carrying a value no
author wrote. The plan here carries exactly the declared set, in exactly ADR-0011's stage
order; what a reader over-reads is *behaviour*, not *declaration*. That is
[FLOWX1027](FLOWX1027.md)'s category at `CS0162`'s severity.

**The rule was narrowed rather than deleted, twice**, from the eight kinds it was written
over to the four no code path applied — `RateLimit`, `Idempotency`, `Cache` and `Audit` —
and then to the two that outlasted stages 1 and 3. `Timeout`, `Retry`, `CircuitBreaker` and
`Bulkhead` left it when the policy engine landed `PolicyStage.Resilience`, which is the same
take-down step [FLOWX1028](FLOWX1028.md) took when `Durable` started running and `FLOWX1031`
took before it was finally deleted. The third narrowing is the deletion above: a rule whose
complement is empty reports nothing, and a rule that reports nothing is a suppression waiting
to be written.

**What did not transfer is [FLOWX1033](FLOWX1033.md)**, which is an **error**, and the two
being adjacent ids about the same DSL call made the distinction worth stating here rather
than only on the pages. FLOWX1032 reported a policy that a *later release* would execute;
FLOWX1033 reports a `CompensationRetry` attached to a step with no compensation, which no
release will ever execute because there is nothing for it to wrap. One was scaffolding for a
missing phase and is deleted now that the phase has landed; the other is a mistake in the
source and is permanent. `StepNode.ForCapability` already refuses that shape with an
`InvalidFlowPlanException`, which is the same relationship `FLOWX1014` and `FLOWX1018` have
to `PolicyChain`'s two rejections — and all three are errors.

## Catalogue

| Id | Rule | Prevents |
|---|---|---|
| [FLOWX1001](FLOWX1001.md) | Flow must be partial | The generated plan has nowhere to live |
| [FLOWX1002](FLOWX1002.md) | Step type is not a capability | A step the engine cannot invoke |
| [FLOWX1003](FLOWX1003.md) | Capability references a transport | Losing quality goal Q4 — the same flow behind any transport |
| [FLOWX1004](FLOWX1004.md) | Capability invokes another capability | Turning the capability set back into a call graph |
| [FLOWX1005](FLOWX1005.md) | Flow inherits from another flow | Control flow invisible to the graph and the manifest |
| [FLOWX1006](FLOWX1006.md) | State-bag contract is outside every generated JSON context | **A durable flow whose journal records nothing for one of its contracts, and a resume that runs the rest of the flow against values no step produced** |
| [FLOWX1007](FLOWX1007.md) | Time is read from the ambient clock rather than the context | A replay reproducing a different instant from the one the journal captured |
| [FLOWX1008](FLOWX1008.md) | Identity or randomness is taken outside the context | **A duplicate charge on a retried step, and a replay minting an id the journal never saw** |
| [FLOWX1009](FLOWX1009.md) | Capability or flow holds mutable state | **Two concurrent invocations of one singleton capability racing on a field** |
| [FLOWX1010](FLOWX1010.md) | Capability declares no authorisation stance | A permissive default nobody chose |
| [FLOWX1011](FLOWX1011.md) | Condition, selector or projection reads something outside the flow's state | A branch that takes a different path on replay, or a step input that is not the journaled one |
| [FLOWX1012](FLOWX1012.md) | Compensation is declared on a flow that is not durable | **A reservation, a hold or an authorisation left standing because the node that would have released it died first** |
| [FLOWX1013](FLOWX1013.md) | Parallel branches must write disjoint context slots | **Two concurrent branches racing on one context slot** |
| [FLOWX1014](FLOWX1014.md) | Retry requires an idempotent capability | **A duplicate charge** — and, since the rule reached the compensation side, **a second reversal**. *Widened once `.WithPolicy` reached the plan made `CompensationRetry` declarable: the analyzer had only ever asked about the step and the `Retry` kind, and the emitter hardcoded every compensation descriptor to idempotent, so a retry over a non-idempotent compensating capability was refused by neither* |
| [FLOWX1015](FLOWX1015.md) | Capability implements more than one contract | Ambiguous dispatch, meaningless manifest entry |
| [FLOWX1016](FLOWX1016.md) | Expected failures are values, not exceptions | A business outcome arriving as a defect alert, missing from the error catalogue and unclassifiable by retry |
| [FLOWX1017](FLOWX1017.md) | AwaitSignal requires the Durable profile | A waiting flow vanishing with its node |
| [FLOWX1018](FLOWX1018.md) | Cache requires no side effects | Reporting a write that never happened |
| [FLOWX1019](FLOWX1019.md) | Flow deadline is shorter than the step timeouts it must contain | A flow that runs out of budget mid-way, reported as a timeout several steps from its cause |
| [FLOWX1020](FLOWX1020.md) | Step consumes a contract no earlier step produces | A flow that throws on its first request |
| [FLOWX1021](FLOWX1021.md) | Sub-flow composition forms a cycle | **A stack overflow, or a deadline breach several flows from its cause** |
| [FLOWX1026](FLOWX1026.md) | Sub-flow cannot be composed | A composition silently missing from the plan, the manifest and the diagram |
| [FLOWX1023](FLOWX1023.md) | Flow declares no steps | A flow that silently does nothing |
| [FLOWX1024](FLOWX1024.md) | Emit step stages no event to publish | A consumer waiting for an event the manifest promised. *Re-scoped once `.Emit<T>()` reached the outbox: the engine stages an emitted event in the step's own transaction and a publisher drains it, so the rule now fires only where that chain cannot start — an `Ephemeral` flow, or a contract no source-generated `JsonSerializerContext` declares. Both have a one-line fix* |
| [FLOWX1025](FLOWX1025.md) | Trigger attribute declares no `[TriggerKind]` | A trigger missing from the manifest, and `flowx diff` unable to tell |
| [FLOWX1027](FLOWX1027.md) | Step is unreachable after `Fail` | A plan, a manifest and a diagram listing work the flow can never do |
| [FLOWX1029](FLOWX1029.md) | Step input mapping produces the wrong contract | A `CS1503` inside generated source, about a call the developer cannot see |
| [FLOWX1028](FLOWX1028.md) | Execution profile is declared but not honoured by the runtime | **A payment saga declaring `Durable` and losing its instance on the next deploy** |
| [FLOWX1030](FLOWX1030.md) | Authorisation stance names no permission or policy | **A capability published as permission-protected that names no permission, and a `flowx diff` rule with nothing to compare when the grant moves** |
| [FLOWX1033](FLOWX1033.md) | `CompensationRetry` is declared on a step with no compensation | **The one policy the runtime executes, dropped by the emitter in silence: a manifest promising five attempts at an undo, and a plan with no undo to attempt** |
| [FLOWX1034](FLOWX1034.md) | Step declares more than one policy set | **A declared timeout, breaker or audit deleted before the plan and the manifest are written, because the second `.WithPolicy(...)` on a step replaces the first rather than adding to it** |
| [FLOWX1035](FLOWX1035.md) | `CompensationRetry` declares a single attempt | A manifest entry that says the undo is retried, over an undo dispatched exactly once — `IsRetrying` is `Attempts > 1`, so one attempt leaves `HasCompensationPolicies` false and the engine takes `CompensationPolicy.None` |
| [FLOWX1036](FLOWX1036.md) | Policy set cannot be read at compile time | **A whole policy set reaching no plan, no manifest and none of `FLOWX1014`, `FLOWX1018`, `FLOWX1019`, `FLOWX1033` or `FLOWX1040` — a shared library's `CompensationRetry` not running, and a duplicate-charge rule with nothing to read** |
| [FLOWX1037](FLOWX1037.md) | Authorisation stance is not enforced by the runtime | **A capability published as policy-protected, diffed as policy-protected, and dispatched with nothing consulting the policy — the one stance of the five the engine cannot decide** |
| [FLOWX1038](FLOWX1038.md) | Scheduled flow cannot be fired | **A published `cron` with no schedule registered behind it: a flow that cannot bind the occurrence and is never started, or an ephemeral one started by every node in the fleet on every occurrence — with no error, no duplicate row and nothing anywhere to count** |
| [FLOWX1039](FLOWX1039.md) | Bus-triggered flow cannot be consumed | **A published `topic` with no subscription registered behind it: a flow that cannot bind the message and is never started, or an ephemeral one started again on every redelivery — with no error, no duplicate row and nothing anywhere to count** |
| [FLOWX1040](FLOWX1040.md) | `Idempotency` is declared on a flow whose result cannot be recorded without redaction | **A replayed transfer answering with an IBAN of `[redacted]` and a `200`: the second caller's money moves to a placeholder, every step reports success, and nothing anywhere says a value was fabricated** |
| [FLOWX1041](FLOWX1041.md) | Change-triggered flow cannot be observed | **A published change subscription with nothing registered behind it: a flow that cannot bind the change and is never started, or an ephemeral one started again every time the cursor is re-read from an uncommitted position — with no error, no duplicate row and nothing anywhere to count** |
| [FLOWX1047](FLOWX1047.md) | Data subject is declared where the runtime cannot record it | **A deployment that runs perfectly and cannot answer an erasure request** — the marker names two members, or the output contract, or a member that is not a string, or a flow that keeps no journal, so the handle is never written and the rows can never be found. Discovered when somebody exercises the right, by which time the identifier the handle would have been computed from has been redacted for months |
| [FLOWX1042](FLOWX1042.md) | Stream-triggered flow cannot be windowed | **A published stream subscription with nothing registered behind it: a flow that cannot bind a window, a non-`Streaming` one whose rebuilt window aggregates a second time after every crash, or a window shape the engine does not implement — a stream nobody reads, and nothing anywhere saying why** |
| [FLOWX1048](FLOWX1048.md) | Triggers on one flow require different input contracts | **Two transports on one class that cannot both be served, and four rules whose advice alternates between them: `FLOWX1038` says declare it as `Flow<ScheduledFire, TOut>` and `FLOWX1039` says declare it as `Flow<BusMessage, TOut>`, and neither can see the other** |
| [FLOWX1046](FLOWX1046.md) | Agent tool declares no confirmation over declared side effects | **A tool a model may call to move money, publishing `confirmationRequired: false`: no client prompts, a server enforcing confirmation has nothing to enforce, and the flow that says so and the capability that charges the card are in two different files** |
| [FLOWX1045](FLOWX1045.md) | Schedule jitter cannot be read | **Every replica of a deployment failing to become ready over a compile-time constant — `FlowSchedule.Create` throws on a `Jitter` it cannot read, and the value was a literal on the attribute the whole way; or a declared `PT0S` that reads as a spread and is not one** |
| [FLOWX1049](FLOWX1049.md) | Stream lateness, checkpoint or parallelism cannot be read | **Every replica failing to become ready over `Lateness = "10s"` — the short form `Window` takes and the one spelling this property does not, printed in docs/09 §9 until `ADR-0065`; or a `Parallelism` of zero, which is a subscription that reads a stream and never runs a flow** |

| [FLOWX1043](FLOWX1043.md) | Poll interval outlasts the poll's own timeout | A `PollUntil` whose first gap is longer than its budget: the instance wakes past it, so the loop is one call followed by the `OnTimeout` block — and one attempt then an escalation reads in a journal exactly like a dependency that never answered |
| [FLOWX1044](FLOWX1044.md) | `PollUntil` requires an idempotent capability | **A second OCR job, a second charge or a second reservation on every attempt of a loop built to make tens of them** — the repetition `Idempotent = true` declares to be safe, asked of a construct that repeats after every success rather than only after a failure |
| [FLOWX1051](FLOWX1051.md) | Trigger decoder does not produce the flow's input | **A subscription registered against a flow no delivery can start** — `Decode` is what lets four contract rules stand down, and it may only do so when the named type actually bridges the transport's payload to the flow's own input |
| [FLOWX1050](FLOWX1050.md) | Step binds a contract only one of a poll's two endings produces | **A flow that works when the webhook fires and throws when the polling does its job** — `.OrSignal<TSignal>()` seeds the bag only on the ending a delivery caused, and both endings continue at the same step |

The next is `FLOWX1052`. The range is `FLOWX1001`–`FLOWX1099`.

> **Every id above is raised and covered by a test.** Four of them were not, until
> WP-13: `FLOWX1014` and `FLOWX1018` ask what is in a policy set, and nothing resolved
> one; `FLOWX1003` and `FLOWX1004` read a capability's dependencies, which the flow
> generator never looks at and which needed a separate `DiagnosticAnalyzer`. All four
> were documented as compile errors the whole time.
>
> `FLOWX1003` has a stated limit worth reading before relying on it: it matches a
> **list** of transport namespaces, not a proof. `FLOWX1020` has stated limits for the
> opposite reason: it is silent wherever it cannot resolve the chain, because a rule
> about step order that fires on a valid flow would be suppressed and then protect
> nothing. `FLOWX1011` has both kinds at once: its scope rules are a proof, its
> catalogue of impure statics is a list, and it is not interprocedural — a helper
> method called from a condition can read a clock and it will not notice. It covers
> every `IFlowBuilder` delegate that takes the flow context, not only `When`; the
> constructs are a table on its page.
>
> The two added in WP-39 state theirs the same way. `FLOWX1016` proves containment — a
> `throw` inside `ExecuteAsync` is a `throw` the engine catches — and *lists* the
> exception types that mean a defect rather than an outcome; it is not interprocedural.
> `FLOWX1019` reports a **floor**: everything it cannot read counts as zero, so its
> silence is never a statement that a flow's deadline is adequate.

## Ids reserved but not yet raised

The catalogue is deliberately smaller than the numbering suggests. Codes appear here
only once the compiler actually reports them — a documented diagnostic that nothing
raises is a promise the compiler is not keeping. Reserved for later phases:
`FLOWX1022` (contract compatibility **across versions** — the analyzer counterpart
of `flowx diff`, distinct from `FLOWX1020`, which checks one flow's steps against
each other). `FLOWX1021` left this list when sub-flows landed; `FLOWX1016` and
`FLOWX1019` left it in WP-39.

`FLOWX1007`–`FLOWX1009` left this list in WP-58, with the meanings every other document
already gave them: ambient clock, ambient identity and randomness, and mutable state on a
capability or a flow. **`FLOWX1012` left it in WP-60**, with the meaning every other
document already gave it too: `.CompensateWith` on a flow whose profile is not `Durable`.
**`FLOWX1006` left it in WP-59**, with the meaning ADR-0008 and ADR-0015's commitment 5 both
gave it: membership of a source-generated `JsonSerializerContext`, checked against the
contracts a `Durable` flow's journal has to write. It was blocked on there being no generated
payload writer to make membership a real requirement, and the row that used to sit below said
so; WP-59 emitted the writer, so the requirement is now one a build can fail on.
It was reserved for longer than any of them, and the row that used to sit below said why
— its remedy. That remedy is now real in both halves: WP-52 made the runtime read
`ExecutionProfile`, and WP-53 and WP-55 gave a host a journal and a lease store to
register, so `Profile = Durable` no longer means either "changes nothing" or "refuses to
run". The rule ships as a **Warning**; the argument, which is *not* the determinism set's
argument, is [below](#the-severity-of-flowx1012-which-is-not-the-determinism-sets-argument).

**What each remaining reservation is blocked on**, so that "reserved" does not
quietly become "forgotten":

| Id | Blocked on |
|---|---|
| `FLOWX1022` | `flowx diff`'s question, asked of two manifests. An analyzer sees one compilation and cannot see the previous version's contracts at all |

**A new rule takes the next id above the catalogue, never a reserved one.** Each
reservation above already has a meaning written down in at least one other document,
and reusing one would leave two rules describing themselves with the same number —
a mistake this project has already made once, when a check was built as `FLOWX1022`
while three documents described it as `FLOWX1020`. `FLOWX1026` took the next free id
for exactly that reason: `FLOWX1022` is spoken for, and "sub-flow cannot be
composed" is not contract compatibility. `FLOWX1027` took the one after it, for the
same reason. `FLOWX1028` is the clearest case yet for the rule: "the runtime does not
honour this profile" is not any of `FLOWX1006`–`1009` or `FLOWX1012`, all of which are
*about* profiles and all of which are spoken for. `FLOWX1029` followed it.

**Two rules were authored against `FLOWX1028` at the same time**, in separate branches,
and the collision was caught at merge rather than by either author — which is the failure
mode this paragraph exists to prevent, arriving from the one direction it did not cover.
Reading "the next free id" is not enough when someone else is reading it too. Claim the id
in this file *first*, in its own commit, before writing the rule.

> [!IMPORTANT]
> **That instruction is now checked, because on its own it does not work.** It was followed
> exactly by both authors of two `ADR-0017`s on 2026-07-31 — each claimed the number in its
> index first, in its own commit — and they collided anyway, because they claimed it from
> the same base commit and neither claim was visible to the other. A claim-first rule
> serialises nothing when the claimants branch from one point.
>
> `IdentifierAllocationTests` in `tests/FlowX.Architecture.Tests` fails the build when an id
> is allocated twice. For this family it checks that no id is catalogued twice, reserved
> twice or release-tracked twice; that no id is both catalogued **and** reserved; that the
> catalogue, the pages in this directory and `AnalyzerReleases.*.md` hold the same set of
> ids; and that the next-free id named at the end of this section is above every id already
> taken and inside the declared range. The descriptors themselves are covered by
> `DiagnosticIdsAreUnique` and `EveryDiagnosticIsReleaseTracked`, which is why this gate does
> not read them a second time.
>
> **What it catches is a duplicate present in one working tree** — the merge, the rebase, or
> one author writing both halves. **It cannot see a duplicate that exists only across two
> unmerged branches**, because a test sees the tree it was built from and CI clones one
> branch. So it reports at the merge, which is where all of these collisions were found by
> hand; what was missing was a check that found them instead of a reviewer.

**`FLOWX1030` is claimed** — *authorisation stance names no permission or policy*:
`Authorization = Authorization.Permission` or `= Authorization.Policy` declared with no
`Permission = "…"` or `Policy = "…"` beside it. It is none of the reservations and it is
not `FLOWX1010`: that rule asks whether a stance was declared at all, and this one
presupposes that it was. [FLOWX1010's page](FLOWX1010.md) already described the gap — its
quick action withholds `Permission` and `Policy` on the grounds that "nothing rejects
`Authorization.Permission` with no `Permission = "…"` alongside it" — and this is the rule
that stops that sentence being true.

**`FLOWX1031` was claimed and is now retired** — *suspension construct is declared but not
honoured by the compiler*: `.AwaitSignal<T>(timeout)`, `.Delay(duration)` and
`.OnTimeout(block)`, all three of which compiled with no diagnostic and produced either no
step or a step that completed immediately. All three are honoured now, so the rule is
[deleted](#flowx1031-is-deleted-with-what-it-described) and the id is **not reused** — a
retired id is retired, because a build log or a suppression referring to `FLOWX1031` means
what it meant, and giving it a second subject would make an old `.editorconfig` line silence
a rule nobody chose.

**`FLOWX1032` was claimed and is now retired** — *declared policy is not executed by the
runtime*: every kind a `.WithPolicy(...)` set declared except `CompensationRetry`, which was
once the only policy any code path in `src/` read. It was not `FLOWX1014` or `FLOWX1018`:
those ask whether a declared policy is *safe* for the capability it wraps and have always been
enforced, and this one presupposed that they passed and asked whether the policy was
*applied*. It was not `FLOWX1028` either — that rule reads a flow's profile, this one read a
step's policy set — though it took that rule's severity argument wholesale. Every kind is
applied now, so the rule is
[deleted](#flowx1032-is-deleted-with-what-it-described) and the id is **not reused**, for the
reason `FLOWX1031`'s is not.

**`FLOWX1033` is claimed** — *`CompensationRetry` is declared on a step with no
compensation*: `FlowEmitter.PolicyArguments` emits the compensation chain only for a step
that `IsCompensable`, so on any other step the one policy this runtime executes is dropped
without a word, while `ManifestWriter` publishes it regardless. It was a separate id from the
retired `FLOWX1032` rather than a second report of it because the two had opposite lifetimes
and opposite severities: `FLOWX1032` was deleted when the last stage landed, and this one is
not, because no release gives a non-compensable step an undo. It is not `FLOWX1014` either — that rule asks
whether the *compensating capability* is idempotent, and presupposes there is one.

**`FLOWX1034` is claimed** — *step declares more than one policy set*: `StepModel.WithPolicy`
assigns `PolicySetName` and `PolicyKinds` rather than adding to them, so the second
`.WithPolicy(...)` on a step replaces the first and everything the first declared is gone
before the emitter and the manifest writer run. It is none of the reservations, and it was not
the retired `FLOWX1032`: that rule reported a policy the plan and the manifest both carry and
no code applies, and this one reports a policy neither of them carries at all. [FLOWX1019's
page](FLOWX1019.md) already recorded the gap — it declines to count a second `.WithPolicy` on
the grounds that "which set wins is a resolution question this rule has no answer to" — and
this is the rule that answers it.

**`FLOWX1035` is claimed** — *`CompensationRetry` declares a single attempt*:
`CompensationPolicy.IsRetrying` is `Attempts > 1`, so `attempts: 1` leaves
`ExecutionPlan.HasCompensationPolicies` false and the engine takes `CompensationPolicy.None`
— one dispatch, which is what a step with no declared chain already gets — while
`ManifestWriter` publishes `CompensationRetry` with its stage and no parameters, so nothing
in the published contract tells it apart from five attempts. It is none of the reservations,
and it is not `FLOWX1033`: that rule asks whether the retry has an undo to wrap, and this one
presupposes that it has and asks whether the count retries anything.

**`FLOWX1036` is claimed** — *policy set cannot be read at compile time*: a `.WithPolicy(...)`
argument that resolves to no initialiser the compiler can walk — a set in a referenced
assembly, one returned by a method, one assembled at run time. `PolicySetReader` returns
nothing rather than guessing, and `FlowEmitter`, `ManifestWriter`, `FLOWX1014`, `FLOWX1018`,
`FLOWX1019`, `FLOWX1033` and `FLOWX1040` are all quiet together on the same argument, which
is not an unchecked policy but an absent one. It is none of the reservations, and it was not
the retired `FLOWX1032`: that rule named the kinds a set declares and said they do not
execute, and this one fires precisely because there are no kinds to name.

**`FLOWX1037` is claimed** — *authorisation stance is not enforced by the runtime*:
`Authorization = Authorization.Policy`, the one stance of the five whose decision the engine
cannot reach. The other four are decided against the invocation's `ClaimsPrincipal` in the
step loop; this one names an ASP.NET Core authorisation policy, which only
`IAuthorizationService` can evaluate, and `FlowX.Runtime` may not reference ASP.NET Core —
`RuntimeIsolationTests` is the gate. It is none of the reservations, and it is not
`FLOWX1030`: that rule asks whether the stance *names* a policy and presupposes the name can
then be checked; this one presupposes that it was named and reports that nothing checks it.
It is the retired `FLOWX1032`'s shape one concept across — a declaration the runtime does not
honour — and it is an **error** rather than that rule's warning, for the reason
[ADR-0030](../adr/ADR-0030-policy-stance-is-refused-at-build-time.md) gives.

**`FLOWX1038` is claimed** — *scheduled flow cannot be fired*: a `[CronTrigger]` the generator
cannot turn into a registration, because the flow's input contract is not `ScheduledFire` — a
firing has no body and only an occurrence to give — or because the flow is not `Durable`, whose
consequence is not that nothing runs but that every node in the fleet runs it, with nothing
journalled to say so. It is none of the reservations, and it is not `FLOWX1025`: that rule asks
whether a trigger attribute declares a kind the compiler can read, and this one presupposes that
it does and asks whether anything can serve the address. It is not `FLOWX1017` either — that
rule requires `Durable` for a construct in the flow's *body*, where this reads an attribute and
has a second reason that has nothing to do with the profile.

**`FLOWX1039` is claimed** — *bus-triggered flow cannot be consumed*: a `[BusTrigger]` or
`[KafkaTrigger]` the generator cannot turn into a subscription registration, because the flow's
input contract is not `BusMessage` — a delivery has only the message to hand over, and it hands
the body over undeserialised because turning it into a typed contract needs a `JsonTypeInfo` only
generated code can name — or because the flow is not `Durable`, whose consequence is not that
nothing runs but that *every redelivery* runs it, with nothing journalled to refuse the second.
It is none of the reservations, and it is not `FLOWX1025`, for the reason `FLOWX1038` is not: that
rule asks whether a trigger attribute declares a readable kind, and this one presupposes that it
does. It is `FLOWX1038`'s rule one transport over and is deliberately a separate id rather than a
widened one — the two name different input contracts and different failure modes, and a
suppression of one must not silently suppress the other.
**`FLOWX1040` is claimed** — *`Idempotency` is declared on a flow whose result cannot be recorded
without redaction*: stage 3 records the flow's state bag through `JournalPayload`, whose only exit
replaces every `[Sensitive]`-named member at every depth, so a flow that marks one member records a
document that is not what it produced — and replaying it hands a later step the literal
`[redacted]` as if it were the value. It is none of the reservations, and it was not the retired
`FLOWX1032`: that rule said a stage is unimplemented, and this one presupposes the stage runs and
reports a declaration it cannot serve. It is not `FLOWX1014` either — that rule asks whether a *capability*
tolerates being called twice, and this asks whether the platform can record what the call produced.
[ADR-0042](../adr/ADR-0042-a-recorded-result-is-replayed-only-when-recording-lost-nothing.md) is the
decision, and it is an **error** for [ADR-0030](../adr/ADR-0030-policy-stance-is-refused-at-build-time.md)'s
reason: the alternative to the rule is not a policy that does less, it is a step that fails at run
time on its first execution.
**`FLOWX1041` is claimed** — *change-triggered flow cannot be observed*: a `[ChangeTrigger]` the
generator cannot turn into a change-subscription registration, because the flow's input contract
is not `BusMessage` — a change is an outbox row and has nothing else to give — or because the
flow is not `Durable`, whose consequence is not that nothing runs but that the flow runs again
every time the cursor is re-read from an uncommitted position, with nothing journalled to refuse
the second. It is none of the reservations, and it is `FLOWX1039`'s rule one transport over,
deliberately a separate id rather than a widened one: a flow may declare both a bus trigger and a
change trigger, and a suppression of one must not silence the other.
[ADR-0047](../adr/ADR-0050-a-change-trigger-observes-the-outbox.md) is the decision. It is
**not** the rule that refuses a flow whose change source is a type it emits — that is a
registration-time refusal in `FlowChangeCatalog.Add`, because the emitted types are in the
`ExecutionPlan` and reading them there reads the artifact that will run.
**`FLOWX1042` is claimed** — *stream-triggered flow cannot be windowed*: a `[StreamTrigger]` the
generator cannot turn into a stream-subscription registration. Two of its three reasons are
`FLOWX1041`'s with the terms changed — the input contract is not `StreamWindowBatch`, or the flow
is not `Streaming`, whose consequence is that the window a crash forces the engine to rebuild is
aggregated a second time rather than refused. The third has no precedent: the declared window is a
shape the engine does not implement, which is a property of the attribute rather than of the flow.
[ADR-0055](../adr/ADR-0055-a-window-names-the-instance-it-starts.md) is the decision, and it is
why only tumbling windows survive. It is an **error** where `FLOWX1028` is a warning, and the
difference is that every one of these three has a fix that produces a flow the engine runs today.

**`FLOWX1048` is claimed** — *triggers on one flow require different input contracts*: two
triggers on one class that fix the flow's input contract to different types. It is the only rule
here that exists because of the other rules: a flow carrying `[BusTrigger]` and `[CronTrigger]`
raises `FLOWX1038` telling the author to declare `Flow<ScheduledFire, TOut>`, and `FLOWX1039` the
moment they do — so this is reported *instead of* `FLOWX1038`, `FLOWX1039`, `FLOWX1041` and
`FLOWX1042` rather than beside them.
[ADR-0062](../adr/ADR-0062-transport-portability-is-a-property-of-the-capability-chain.md) is the
decision, and it is where the shape of quality goal Q4's claim is settled: portability holds over
the capability chain, one adapter step in, and not over a single flow class. Kinds that agree on a
contract are not in conflict — `Bus` and `Change` both take `BusMessage`, which is the
two-subscriber arrangement `samples/event-driven` ships.

**`FLOWX1046` is claimed** — *agent tool declares no confirmation over declared side effects*:
`[AgentTrigger(Confirmation = ConfirmationMode.Never)]` on a flow whose steps reach a capability
declaring `SideEffects`. A tool descriptor's `confirmationRequired` is the declared mode resolved
against that union — `RequiredForSideEffects`, the attribute's default, is true exactly while the
union is non-empty — so `Never` publishes `false` for a call with a consequence, no client prompts
before it, and a deployment running `ConfirmationPolicy.Elicit` elicits nothing.
[ADR-0060](../adr/ADR-0060-the-server-asks-the-caller-for-what-it-does-not-have.md) is the decision
that makes the annotation load-bearing. It is none of the reservations, and it is not `FLOWX1030`:
that rule asks whether an authorisation stance names anything, and this presupposes the stance is
fine and asks whether a *human* is told. It is a **warning** where `FLOWX1042` is an error, and the
difference is that the declaration is sometimes right — a cache write and a search-index update are
declared side effects too — so the author who means it writes one `#pragma` with a reason, which is
a decision a reviewer can read.

**`FLOWX1047` is claimed** — *data subject is declared where the runtime cannot record it*: a
`[Subject]` marker the runtime would have to ignore. Four shapes, one rule and one suppression,
unlike `FLOWX1038`–`FLOWX1042`, which are four rules about four attributes precisely so that a
suppression of one does not silence another. These four are one rule about one attribute — *the
handle will never be written* — and a project that legitimately suppressed it for one of them
would legitimately suppress it for all of them, because the consequence is identical in every
case: rows no erasure can ever find.
[ADR-0061](../adr/ADR-0061-a-subject-is-erased-by-digest-and-a-residency-is-a-refusal.md) is the
decision. It is an **error** on `FLOWX1038`'s argument: every shape has a one-line fix that
produces a flow this runtime serves today.

**`FLOWX1045` is claimed** — *schedule jitter cannot be read*: a `[CronTrigger]` whose `Jitter`
is not a positive ISO-8601 duration. Unlike `FLOWX1038` this is a property of the **declaration**
rather than of the flow, so it is reported per attribute: a flow with two schedules can have a
readable spread on one and rubble on the other, and a suppression written against the second must
not silence the first. The consequence is that every replica of the deployment fails to become
ready — `FlowSchedule.Create` throws on a spread it cannot read, for the reason it throws on an
expression it cannot read — over a value that was a compile-time constant the whole way. A
declared `PT0S` is refused with the rest, because asking for a spread and getting none reads as
working; an **omitted** property is the ordinary declaration and is silent.
[ADR-0059](../adr/ADR-0059-schedule-jitter-is-derived-from-the-firing.md) is the decision the
value belongs to. It is none of the reservations.

**`FLOWX1043` is claimed** — *poll interval outlasts the poll's own timeout*: a `PollUntil`
whose first gap is longer than the budget it declares. A poll makes its first attempt
immediately and parks for the interval before the second, so the instance wakes after its budget
has gone and takes the escalation — the loop is a single call, and nothing about the instance
says so. It is a **warning**, on `FLOWX1019`'s argument: the flow runs, both durations are legal
C#, and an author who wants exactly one attempt and a fallback has written it in an obscure way.
It is silent whenever either duration is one `DeclaredDuration` or `DeclaredBackoff` cannot
evaluate, which is `FLOWX1019`'s stance again — a rule that guessed at a schedule read from
configuration would fire on flows that are correct at run time.

**`FLOWX1044` is claimed** — *`PollUntil` requires an idempotent capability*: a poll invokes its
capability once per attempt, with one request's worth of input and one idempotency key, until a
condition holds. That is exactly the repetition `Idempotent = true` declares to be safe, and it
is `FLOWX1014`'s argument reached by a different door — the stronger of the two, because a retry
repeats only after a failure and a poll repeats after every success. An **error** where
`FLOWX1043` is a warning: a declaration whose two durations disagree produces a flow that runs
and reads oddly, and this produces a flow that runs correctly the first time and creates a second
OCR job on the second attempt. [ADR-0058](../adr/ADR-0058-a-poll-is-one-wait-not-a-race-between-two.md)
is the decision the pair belongs to.

**`FLOWX1049` is claimed** — *stream lateness, checkpoint or parallelism cannot be read*: a
`[StreamTrigger]` whose `Lateness` or `Checkpoint` is not a non-negative ISO-8601 duration, or
whose `Parallelism` is below one. It is `FLOWX1045` one transport over, including the reason it is
its own id rather than part of `FLOWX1042`: that rule asks whether the *flow* could be windowed
and judges the window's shape family, this asks whether *this declaration's* remaining three
arguments can be read, and a flow may declare two streams. `StreamWindowSpec.Read` takes four
arguments and the split between the two rules is exactly the split between the first and the other
three. Zero is refused for `Parallelism` and accepted for both durations, which is `Read`'s own
boundary. [ADR-0065](../adr/ADR-0065-a-window-is-declared-where-it-is-served.md) is the decision
the value belongs to, and the reason this rule was worth writing: the page that taught the
declaration printed `Lateness = "10s"`, which no build and no test read.

**`FLOWX1050` is claimed** — *step binds a contract only one of a poll's two endings produces*: a
`.PollUntil<T>(…).OrSignal<TSignal>()` leaves its wait two ways, and both continue at the same
index. The predicate ending leaves the attempt's own output in the state bag; the delivery ending
leaves `TSignal` as well. A step after the poll that binds `TSignal` therefore runs on one of them
and throws on the other — the other being the path a poll exists for. It is `FLOWX1020`'s argument
narrowed to the one construct that produces conditionally, and is reported *instead of*
`FLOWX1020` on that line rather than beside it, because the type genuinely is in the bag and the
two rules would otherwise give opposite advice. An **error**, for `FLOWX1020`'s reason: there is
nothing probabilistic about which paths exist.
[ADR-0066](../adr/ADR-0066-a-polls-second-ending-is-a-row.md) is the decision it belongs to.

The next is `FLOWX1052`. The range is `FLOWX1001`–`FLOWX1099`.

## Adding a diagnostic

1. **Claim the id first, in its own commit**: add the catalogue row and move the
   "the next is …" sentence above to the id after it. Both halves are checked —
   a claim that does not advance the pointer leaves it aimed at an id you are
   already using, and hands it to the next two people who read it.
2. Add the descriptor to `FlowXDiagnostics`, with a message naming the offending
   symbol, a description saying what to do instead, and a help URI.
3. Add the id to `AnalyzerReleases.Unshipped.md`. The build fails without it —
   RS2008 — which is intentional: a diagnostic id is public surface, because teams
   write suppressions against it.
4. Write the page in this directory.
5. Add the test that proves it fires, and the test that proves it does not fire on
   valid code. The second one matters more; a rule with false positives gets
   suppressed everywhere and then protects nothing.

Step 1 does not make a collision impossible — nothing a single branch does can — but
`IdentifierAllocationTests` makes it impossible to merge one without the build going red.

---

**Back to:** [Quality gates](../21-Quality-Gates.md) · [Architecture](../05-Architecture.md)
