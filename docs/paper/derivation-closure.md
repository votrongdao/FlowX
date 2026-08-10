# Derivation Closure: Making a Service's Integration Surface a Function of Its Compiled Graph

**Draft — target venues: ICSA *Software Architecture in Practice*, ICSE *SEIP*, EASE.**

**Status:** working draft. Every quantitative claim in §5 is reproducible from this
repository; Appendix A gives the command or file for each one. Nothing in this document
is estimated, extrapolated, or carried between sections without re-reading its source.

---

## Abstract

A service's *integration surface* — its HTTP routes, its callback endpoints, the topics it
consumes, the queue a poison message lands in, the address a scheduler calls, the tool
descriptor an agent reads — is normally **declared a second time**, in configuration, next
to the code that implements it. Every one of those second declarations is a copy, and every
copy drifts.

We describe **derivation closure**: an architectural rule that *no externally addressable
surface may exist unless it is a total function of the compiled application graph*, together
with a **contract-admission rule** that governs how that graph's published schema is allowed
to grow, and a set of build-failing fitness functions that enforce both. The composition is
the contribution; each part has partial prior art, and §7 says exactly which.

We report three years' worth of nothing — this is a single-system, single-team study over
**68 architecture decision records**, **7 transport adapters** and **88 executable fitness
functions** — and we report it honestly, including the parts where the approach lost. The
headline cost is that compile-time derivation makes builds **+67.1 % slower at 200 flows
(95 % CI [+61.9, +73.6]) against a +8 % budget: a stated, unmet, non-negotiated FAIL.**
Closure is also not uniform: three records document surfaces where the rule could not be
held and says so at the point of failure rather than in a footnote.

The transferable results are: (i) the admission rule demonstrably refuses fields, and we
show the record where it did; (ii) **20 of 80 published schema field paths have no
producer** — the debt the rule was invented to stop accruing, and larger than the project's
own hand audit had recorded, which is the argument for computing such a set rather than
maintaining it; and (iii) on a shared CI runner, a build-cost gate must
measure **bytes allocated, not wall clock** — twelve identical runs of one tree disagreed on
wall clock by **139 %** while allocation moved by **0.069 %**.

---

## 1. Introduction

Consider a workflow that reserves inventory, captures a payment, then waits for a human to
approve a refund. To deploy it you must, today, in essentially every mainstream stack:

1. write the workflow;
2. write an HTTP route that starts it;
3. write a second HTTP route that delivers the approval signal;
4. write the OpenAPI description of both;
5. write the topic binding if the trigger is a message rather than a request;
6. write the dead-letter destination for when that message cannot be processed;
7. write the cron expression and the endpoint the scheduler will call;
8. write the dashboard that names the steps;
9. write the tool descriptor if an agent is meant to invoke it.

Items 2–9 are all *restatements of facts item 1 already contains*. They are maintained
separately, reviewed separately, and deployed separately, so they diverge — not eventually,
but on the first change that touches one and not the others. This is the oldest complaint in
software architecture and it has an equally old answer: *derive the restatements*.

The answer is old and yet only partially applied, and we think the reason is that
"derive the restatements" is normally implemented as **a generator**, and a generator is
opt-in. Anything a generator does not cover is still hand-written, and the hand-written
remainder is exactly where the drift concentrates. A generator that covers 80 % of the
integration surface does not reduce drift by 80 %; it relocates all of it into the other
20 % and removes the reviewer's expectation that anyone is watching.

**What we did instead** was state the rule as a closure property and enforce the closure
rather than the generation:

> **D1 (derivation closure).** Every externally addressable surface is a pure function of
> the compiled application graph. No adapter may accept a hand-written address.

D1 alone is not enough, because the obvious way to satisfy it is to let the graph grow a
field for every address any adapter wants — at which point the graph is the configuration
file, wearing a different name. So the graph's published schema needs an admission rule:

> **D2 (contract admission).** A field enters the published schema only if, in the same
> commit, (a) something produces it from the code, and (b) a compatibility classifier rules
> on what a change to it means. Deployment tuning is excluded by construction: if the value
> answers *how this deployment runs*, rather than *what this application is*, it is not
> admissible.

And neither rule survives being a convention:

> **D3 (enforcement).** D1 and D2 are executable fitness functions that fail the build, and
> each one is itself proven capable of failing.

§3 develops the three rules; §4 describes the implementation; §5 evaluates it, cost first.

**Contributions.**

1. Derivation closure stated as a checkable architectural property rather than as a codegen
   feature, with the admission rule (D2) that keeps it from degenerating (§3).
2. A longitudinal account of applying it across 7 transports, including the three surfaces
   where it could not be held and what was done instead (§5.4).
3. Quantified cost, negative: **+67.1 %** build overhead against a **+8 %** budget (§5.3),
   and the measurement-methodology finding that makes such a gate possible on shared CI at
   all (§5.5).
4. The debt measurement, made executable: **20 of 80** field paths in a published contract
   schema have no producer, computed by a build-failing gate rather than by audit — and the
   audit it replaces had missed 8 of them (§5.2).

---

## 2. The problem, precisely

### 2.1 A second declaration is a second source of truth

The industry's standard mitigations are *contract-first* (Protobuf, Smithy, OpenAPI-first)
and *model-extraction* (Structurizr-for-code, jQAssistant). Both reduce the number of
declarations to two rather than to one:

* **Contract-first** makes the contract authoritative and derives client and server stubs
  from it. The implementation is then free to disagree with the contract in every way the
  stub does not constrain — most importantly, in *which* addresses actually exist at
  runtime, because a stub that is never wired is a route that is never served.
* **Model-extraction** makes the code authoritative and derives a model from it. But the
  extracted model is *descriptive*: nothing consumes it at runtime, so nothing breaks when
  it is wrong. A description that cannot fail is a description nobody checks.

Derivation closure is the third position: the code is authoritative, the derived artifact is
*prescriptive* — the adapters read it and there is no other place for an address to come
from — and a build-failing check asserts the closure.

### 2.2 Why "the manifest" is not the contribution

Emitting a machine-readable description of an application at build time is not new and we do
not claim it (§7). The claim is about what is *forbidden*: that an adapter may not be
configured with an address, that the schema may not grow a field on the promise of a future
producer, and that both prohibitions are tested.

The distinction is observable. A project that merely emits a manifest accumulates fields
faster than producers, because a field is cheap to declare and a producer is expensive to
write. We can measure exactly how fast, because our own schema was written before D2 was:
**20 of 80 field paths are unproduced** (§5.2). D2 is the rule that stopped the next one, and §5.2 shows the record where it did.

---

## 3. The approach

### 3.1 D1 — an address is derived, never declared

The compiled graph names flows, their steps, the capabilities each step calls, the triggers
that start them, the events they emit, and the waits they can suspend on. Every address in
the integration surface is a function of some subset of that:

| Surface | Derived from |
|---|---|
| Route that starts a flow | flow name and version |
| Route that delivers a signal to a suspended flow | flow name × signal name |
| HTTP status of a suspension | the fact that a wait exists on the plan |
| OpenAPI document | the whole graph |
| Topic/consumer-group binding | the trigger declaration |
| Dead-letter destination | the trigger's address, transformed |
| Address a schedule calls | flow name and version |
| Metric label set | the steps that actually execute |
| Agent tool descriptor | capabilities marked exposable |

**One class, several addresses — as far as the type system allows, and no further.** A flow
carries its triggers as attributes, and stacking them yields one address each from a single
declaration: `booking.book` declares `[HttpTrigger]` and `[AgentTrigger]` and the build emits
both an HTTP route and an agent tool descriptor, from one class, with the business steps
written once. A test drives the same booking down both addresses in one process and requires
the same reference and the same total, so this is a property of the running system rather
than of the generated text.

**The limit is worth stating precisely, because it is the one place the model costs
something.** A trigger that delivers a payload dictates what the flow's input type must be: a
schedule can hand over only its occurrence, a bus delivery or an outbox change only the
message, a closed window only its records. A class has one input type, so two triggers from
that group cannot share one — the compiler refuses it as an error, with a diagnostic that
exists specifically because the four narrower rules would otherwise send an author round a
loop, each telling them to declare the contract the next one rejects. HTTP and agent triggers
are exempt: they bind whatever the flow already declares, which is why that pair stacks and
the others do not. The consequence is visible in our own samples, where one business
operation reachable over four transports is four classes whose step bodies are byte-identical
below the first decoding step. Portability therefore holds over the capability chain rather
than over the flow class, and a design that moved the decoder onto the trigger attribute
would close the gap — that is not built, and the paper should not imply it is.

**One declared root per trigger, and everything else derived.** The precise form of the rule
matters, and the table above understates it in one direction and overstates it in another.
The author *does* declare one address — the route on an HTTP trigger, the topic on a bus
trigger, the expression on a schedule — because those are genuinely the application's to
choose and there is nothing to derive them from. What D1 forbids is the *second* address:
the signal endpoint, the dead-letter destination, the callback the scheduler uses, the
operation id in the generated document. Each of those is a function of the declared root
plus the graph, and none of them appears in any configuration file.

The deployment still has configuration, and D1 is a statement about what may be in it. A
transport's options record carries roots and shapes — a broker's bootstrap servers, the root
topic, the prefix a queue name is built from, the suffix a dead-letter destination takes —
and no per-flow name at all. That is the checkable form, and it is what our gate asserts: no
string literal in any transport's options may name a flow, a capability or an event that a
real compilation produced. A second gate asserts the other direction — that every trigger
kind with an external address has a compiler emitter that derives it, and that every emitter
belongs to a trigger kind. Both were proven by a mutation that compiles: adding
`public string PlaceOrderTopic { get; init; } = "order.placed";` to one transport's options
turns the first red, and an unclassified emitter turns the second red.

The consequence a practitioner feels immediately is that **renaming a flow renames its
signal endpoints, its scheduler callback, its dead-letter destination and its OpenAPI
operation ids in one commit, and a compatibility classifier reports the change**, which is
precisely what a hand-maintained configuration cannot do.

The consequence a practitioner feels three months later is the constraint: **you cannot
special-case one deployment.** §5.4 covers the three times this hurt.

### 3.2 D2 — the contract's own admission rule

The graph is published as a versioned JSON document with `additionalProperties: false` at
every level, which means the schema is a closed contract and every field in it is a promise.
D2 governs the promise:

1. **A producer exists.** Not "will exist" — exists, in the same commit. The failure mode
   this prevents is a consumer being unable to distinguish *absent because the application
   does not have one* from *absent because nothing ever looked*. Those are opposite facts
   and an empty field reports them identically.
2. **A classifier exists.** Something must rule on what a *change* to this field means:
   breaking, additive, or neutral. A field with no classifier is a field that can change
   silently, which makes the whole document's compatibility report a subset claim rather
   than a total one.
3. **It is not deployment tuning.** Concurrency limits, scan intervals, retry ceilings and
   delivery caps describe *this deployment*, not *this application*. Publishing them would
   put operational configuration into a contract document and hand the compatibility
   classifier a whole class of changes no consumer could act on.

Rule 3 is the one that does the most work, because it is the rule that prevents D1 from
being satisfied trivially. Without it, every adapter that wants a knob adds a field, the
graph becomes the union of all adapters' configuration, and the "single source of truth" is
a single *file* containing many sources of truth.

### 3.3 D3 — enforcement, and enforcement that can fail

Both rules are architecture tests that run in the ordinary build. The second clause matters
more than the first. A gate is only evidence if it has been shown to fail on the thing it
claims to catch, and we adopted the practice of proving each one by a *compiling* mutation:
change the production code so the property is genuinely violated, confirm the build still
compiles, and confirm the test goes red. A non-compiling mutation proves nothing — it is a
false red — and a skipped test is a false green.

This is more than hygiene. §5.5 records a build-cost gate that ran green for months while
the thing it measured regressed by 4.9×, because the gate compared against an absolute
budget the project was *already* failing. It reported the same value before and after the
regression. It was, in the only sense that matters, not a gate.

---

## 4. Implementation

The subject system is a .NET workflow platform. Flows are written in a typed builder DSL; a
Roslyn source generator compiles each flow into an execution plan and emits the graph as a
build artifact alongside the assembly. Adapters for HTTP, PostgreSQL, Redis, Kafka,
RabbitMQ, Azure Service Bus and an agent tool protocol consume the plan.

Three implementation choices are worth naming because they are what make D1 enforceable
rather than aspirational:

**The DSL exposes no transport type.** The builder surface is checked by a fitness function
asserting that no type reachable from it names a transport, and by a second one asserting
that no flow's transitive closure reaches a transport assembly. If a flow *could* name a
broker, D1 would be a coding convention rather than a property.

**Refusal is a compile error, not a runtime default.** 48 diagnostic identifiers are raised
by the compiler. The pattern the paper cares about is the one where a declaration that
*cannot be resolved at build time* is refused rather than defaulted — for example, a
capability declaring an authorization stance whose value the compiler cannot see. The
alternative, skipping the unresolvable case at run time, produces a system where the graph
is silently less true than it appears, which is D1 failing quietly.

**The classifier ships with the field.** Compatibility classification is a pure function
over two graph documents producing a report; it currently carries **40 classification
rules — 22 breaking, 9 additive, 9 neutral**. D2's clause (b) is satisfied by adding a rule
here in the same commit as the producer.

---

## 5. Evaluation

### 5.1 What the corpus is

| Measure | Value |
|---|---|
| Architecture decision records | 68 (67 Accepted, 1 Proposed) |
| ADRs carrying a `Revisit when` clause | 68 / 68 |
| Executable fitness functions | 88 declared |
| Compiler diagnostic identifiers | 48 |
| Transport / infrastructure adapters | 7 |
| Published schema field paths | 75 |
| Compatibility classification rules | 40 (22 breaking, 9 additive, 9 neutral) |

This is one system built by one team. §6 is explicit about what that does and does not
support.

The `Revisit when` figure is worth one sentence beyond its row. Every record states the
condition under which it should be reconsidered, which turns the corpus into something that
can be *audited against reality* rather than merely read. §5.3 is the result of doing that
audit, and it is not flattering.

### 5.2 Does the admission rule actually refuse anything?

Yes, and the interesting evidence is a record of a refusal rather than a record of an
addition.

When message-bus consumption was implemented, the natural instinct was to publish what the
new subscriber knew: its in-flight limit, its dead-letter destination, and the set of flows
consuming each event. D2 refused all three, for a different clause each time:

* **In-flight limit** — clause 3. It is tuning: it describes how this deployment runs a
  subscription, and a consumer of the contract cannot act on a change to it.
* **Dead-letter destination** — clause 1, in an unusual form. The destination is *derived*
  by the adapter rather than declared by the author, so publishing the declared value would
  publish a string nothing uses. The field would have had a producer and still been a lie.
* **The set of consuming flows** — this one was admissible in principle and was declined on
  scope: publishing it from a single compilation's subscriptions would describe one service,
  while the field's meaning is estate-wide.

The outcome is a decision record whose decision is *the schema gains no field*. We regard
this as the strongest available evidence that D2 is load-bearing: a rule that has never
produced a refusal is indistinguishable from a rule nobody applies.

**And the counterfactual is measured, because it is our own history.** The schema was
written before D2, and **20 of its 80 field paths have no producer**. That number is itself a
result about method: the project had audited the same question by hand and recorded thirteen,
and the audit was wrong in the direction audits are always wrong — it compared field *names*
against the names the writer emits, so it missed the entire top-level `policies` catalogue,
five paths whose leaf name is written at a different path. Replacing the audit with a set
difference between the schema's paths and the paths present in every manifest the repository
emits found those five, plus three more. The gate now fails the build when a field enters the
schema without a producer and no row records why, and fails again when a row survives its own
field being closed, so the set can shrink and cannot silently grow.

The debt falls into three kinds:

* *the fact is not declared anywhere* — ownership, deprecation, partition keys: the DSL has
  no way to state them, so closing the field means a language addition;
* *the fact exists and the compiler does not carry it* — the symbol's source location, the
  reviewer recorded in an attribute: small work, never scheduled;
* *the fact requires machinery that does not exist* — per-type JSON Schema, which is what
  the OpenAPI, AsyncAPI and agent-tool descriptors would be generated from.

Two of the original thirteen closed during this period, and the way each closed is the
argument for D2's clause (b). The second was `event.producedBy`: the compiler already walked
every `Emit` step of every flow to build the event catalogue and then discarded which flow
each one came from, so the producer cost a second pass over a list already in hand — and it
shipped in the same commit as the rule that classifies a change to it. The first is the one
worth dwelling on. An authorization field was published with the *mode* but not the *value*:
the sample application declared a permission, and the compiler dropped it between reading
the declaration and writing the graph. The consequence was not merely a thin document — it
was that a breaking-change rule *could not fire*, because half of what it compared was
always empty. A field with no producer had silently disabled a classifier that did exist.

### 5.3 What it costs: the negative result

Compile-time derivation is paid for at compile time. We budgeted **+8 %** build overhead and
we do not meet it.

| Flows | Overhead vs. the same solution without the generator |
|---|---|
| 50 | **+46.5 %**, 95 % CI [+42.4, +51.0] |
| 200 | **+67.1 %**, 95 % CI [+61.9, +73.6] |

Against a **+8 %** budget, at 200 flows, with an A/A noise floor of 10.6 % and within-arm
IQRs of 7.3 % and 7.5 %: **FAIL**, and not marginally.

Two secondary findings make the failure interpretable rather than merely embarrassing.

**The cost is linear in flow count with no fixed term.** A power-law fit gives
`flows^0.91`, 95 % CI **[0.82, 1.08]** — an interval containing 1.0 — which reads as roughly
constant milliseconds per flow. This is the good news inside the bad: the approach does not
degrade super-linearly as an application grows. It is uniformly too expensive per flow, and
uniform-and-too-expensive is an optimization problem, whereas super-linear would have been a
design problem.

**Almost all of it is one component.** The plan generator is **90.5 %** of the measured
per-flow cost. A bisect attributes a 4.9× step change in that component to a single commit —
the one that began deriving each capability's error catalogue from its body — and 93 % of
the added cost is semantic-model type resolution, because deriving what a method can fail
with means binding the code that fails. Removing a duplicated bind recovered 20 %. The rest
is what the feature costs.

We record this as an unresolved cost rather than a solved one. The decision record that the
entire compile-time bet rests on states its own revisit trigger as *"build overhead > 8 %
sustained"* — **the trigger has fired, and it fired silently.** The number was measured, the
record was not amended, and nothing connected them. That is a finding about the practice of
keeping such records, not only about this system, and §8 draws the lesson.

### 5.4 Where closure was not achieved

Three surfaces resisted, and in each case the response was to record the boundary rather
than to weaken the rule quietly.

**A broker that already owns the concept.** One broker family provides native dead-letter
routing. Under D1 the destination is derived from the trigger's address; the broker offers
to derive it too, differently. Adopting the broker's version would make the destination a
property of the deployment rather than of the graph, and the same flow on two brokers would
dead-letter to two differently-shaped addresses. The decision keeps the derived destination
even where the broker has one, accepting redundant configuration in exchange for one
address rule across all transports.

**A broker whose partitioning is not a property of the message.** Per-key ordering is
derivable where the transport has partitions. On one broker the analogous unit is a queue,
and it behaves as a partition *only while a single node holds it* — which is a property of
the running deployment, not of the graph. The graph therefore cannot honestly publish an
ordering guarantee for that transport, and the record says so instead of publishing one that
is true in the common case.

**A surface whose derivation needs a fact the graph does not carry.** A database change-feed
trigger observes a row change and must start a flow *for the tenant whose data changed*. The
flow's address derives cleanly; the tenant does not, because tenant identity arrives on an
invocation as an attested claim and a background scan has no principal to attest with. The
platform's one flag for carrying a tenant without claims exists for *continuing an instance
already admitted*, and it also skips step authorization — right for a recovery scan, wrong
for a start. Deriving the tenant from the observed row would have been the easy move and the
dangerous one: the scan would read its own refusal as progress and commit its cursor past a
change no flow ran. The trigger therefore **refuses to be wired at all** in that topology,
with the refusal naming the decision that is missing. Under D1 this is a genuine gap — a
surface that cannot be derived — and the response was to leave it underived rather than to
derive it from a fact nobody stated.

Two generalizable observations. **Closure fails where a transport has its own opinion about
an address** — the first two cases — which is predictable and therefore plannable, and a
better place to spend design attention than the surfaces that derive cleanly. And **closure
fails where the derivation needs an input the graph was never given**, the third case, where
the only honest options are to refuse the surface or to extend the graph; the tempting third
option, deriving from something adjacent, is how a cursor commits past unprocessed work.

### 5.5 A gate on a shared runner must not measure time

This began as an engineering annoyance and turned out to be the most transferable result.

The build-overhead gate was originally a wall-clock comparison against the absolute +8 %
budget. It never went red at the 4.9× regression above — because the project was already
failing the budget by ten points, so the gate reported the same verdict before and after.
An absolute gate on a budget you already miss is not a gate.

The replacement is *relative* — against a committed baseline, on every pull request — and it
does not measure wall clock, because on the available runners wall clock cannot resolve the
signal. Over twelve identical runs of one tree:

| Metric | Spread across 12 identical runs | Reading of the real 4.9× regression |
|---|---|---|
| Wall clock | **±139 %** | +77 % |
| Bytes allocated by the generator | **±0.069 %** | +102 % |

The regression is *smaller* than the noise in the wall-clock metric and three orders of
magnitude larger than the noise in the allocation metric. The gate now blocks on allocation
at a +2 % threshold, and reprints the (still failing) wall-clock budget on every run so that
a green relative gate cannot be misread as a met budget.

**The replacement then caught its author.** Closing `event.producedBy` (§5.2) was
implemented the obvious way — for each event, ask which flows emit it — and the gate blocked
it at **+3.30 %**, attributed by a control run to that change alone. Rewritten as one pass
building a producer index, the same field costs **+0.40 %**, and the residual is the
characters the field adds to the emitted document, which the gate reports separately as an
advisory. We report this because a gate that has only ever caught other people's regressions
is a weaker claim than one that caught the change being written to demonstrate it.

**And one attempted optimization measured as nothing, so it was not shipped.** The
error-catalogue reader calls `Compilation.GetSemanticModel` once per followed symbol, and a
fresh model caches no bound nodes — textbook Roslyn waste. Caching one model per syntax tree
per scan measured **+0.16 %** against its own control, inside the noise floor: in a corpus
where each capability sits in its own file, the reader rarely follows across trees, so the
cache had nothing to reuse. The change was dropped. An optimization that cannot be
distinguished from noise is a change with a cost and no benefit, and the discipline that
makes the rest of these numbers worth reading is the one that deletes it.

The lesson generalizes past this project: **on shared or virtualized CI, prefer a
deterministic proxy metric with a demonstrated noise floor over the metric you actually care
about, and state the substitution in the gate's own output.** The substitution is only
defensible if the noise floor is measured — which is why the A/A control run in §5.3 is
reported alongside every overhead figure.

---

## 6. Threats to validity

**Construct.** "Integration surface" is our definition and it is drawn where our adapters
are. A system with a surface we do not have — a GraphQL schema, a gRPC reflection service, a
UI route table — might find the closure property easier or harder to hold, and we cannot say
which.

**Internal.** The build-cost measurements come from one machine class. The A/A control and
the confidence intervals bound the *scatter*, not a systematic difference between arms
caused by, for example, disk caching favouring whichever arm ran second; the alternating
A B A / B A B ordering mitigates this but does not eliminate it. Allocation figures are
environment-specific: the same commit measured different byte counts locally and on CI, and
we say so rather than quoting the friendlier number.

**External — this is the largest.** *n* = 1 system, one team, one language ecosystem, and
the team that designed the rule is the team that evaluated it. Every claim about the rule
"working" is a claim about it working here. What we believe does transfer is the *shape* of
the argument: that a derivation rule needs an admission rule, that both need a gate proven
capable of failing, and that the failures cluster where transports have opinions.

**Conclusion validity.** We report a decision record that declines to add a field as
evidence the admission rule bites. A skeptic can reasonably say we could have written that
record either way. The counter-evidence is the 20 unproduced fields: before the rule existed,
the same team added fields it never produced, repeatedly. The rule changed the outcome for
the same team on the same schema.

---

## 7. Related work, and what we do not claim

The problem is old and well characterised. Perry and Wolf [1] separate *erosion* — violating
an architectural principle — from *drift*, which is insensitivity to the architecture rather
than a violation of it; the second declarations §2.1 describes are drift in that sense, since
nobody violates anything by editing a route in one place and not the other. Li et al.'s
systematic mapping study [2] finds that most detection work targets architectural
*consistency*, and that erosion is caused by technical and non-technical factors together —
knowledge vaporisation among them, which is what a fact stated twice and maintained once
becomes. De Silva and Balasubramaniam [3] survey the control side.

**We do not claim novelty for checking an implementation against an architectural model.**
Murphy, Notkin and Sullivan's reflexion models [4] map source entities onto an architectural
model and compute convergences, divergences and absences — the shape every conformance
checker since has taken. Two things differ here. A reflexion model needs a *separately
authored* high-level model, which is the second declaration; and it reports divergence rather
than preventing it, because nothing at runtime consumes the model.

**We do not claim novelty for emitting a machine-readable architecture artifact.** Model
extraction from code (Structurizr-for-code, jQAssistant, Moose), architecture description
languages, and build-time IDL emission all predate this by decades. What those approaches
produce is *descriptive*: nothing at runtime depends on it, so nothing fails when it is
wrong.

**We do not claim novelty for executable fitness functions.** Ford, Parsons, Kua and
Sadalage [5] name the concept and the practice of running them in a deployment pipeline; D3
is that practice applied to D1 and D2, plus one addition we do think is worth stating — that
a fitness function is evidence only once a *compiling* mutation has been shown to turn it red
(§8, lesson 3).

**We do not claim novelty for compatibility classification of a published contract.**
`buf breaking` [6] compares a Protobuf schema against a past version and reports what would
break clients; `oasdiff` [7] does the same for OpenAPI and classifies, by its own count, 509
distinct kinds of change — an order of magnitude past our 40, because their contracts are an
order of magnitude wider. What neither does is condition the contract's *growth* on a
classifier existing: a new field is admissible in both, and only later becomes something a
diff can rule on. D2's clause (b) is the difference, and it is a small one to state and a
large one to hold.

**We do not claim novelty for contract-first development.** Smithy, Protobuf and
OpenAPI-first invert the same relationship we do, in the other direction: the contract is
authored and the code derived. That is a coherent alternative, and §2.1 gives the reason we
did not choose it — a generated stub that is never wired is a route that does not exist.

**We do not claim novelty for compile-time orchestration or source-generated dispatch.**
Source-generated mediators and compile-time dependency injection in the same ecosystem
already demonstrate that a message-dispatch graph can be resolved at build time with no
reflection and no per-call allocation. A paper about *that* would be a paper about prior
art. Our generator's output is a means to the graph, not the contribution.

**We do not claim novelty for durable execution.** Journal-and-replay workflow engines
establish the recovery model our durable profile uses.

**What we claim** is the composition and its enforcement: closure as a stated property over
the whole integration surface, an admission rule that prevents the published graph from
degenerating into a configuration file, gates for both that are proven capable of failing,
and a longitudinal account of the three places the closure broke.

---

## 8. Lessons for practitioners

1. **A derivation rule without an admission rule degenerates.** Every adapter that wants a
   knob will propose a field. Decide in advance that *how this deployment runs* is not
   *what this application is*, and the proposals answer themselves.
2. **Require the classifier in the same commit as the field.** We have direct evidence of
   the failure mode: a field published without its value silently disabled a breaking-change
   rule that already existed. Nobody noticed, because a rule that cannot fire looks exactly
   like a rule that has nothing to report.
3. **A gate must be shown to fail, and "shown" means the mutation was actually run.** Prove
   it with a mutation that *compiles* — a non-compiling one is a false red. Two of ours passed
   review and failed the demonstration: a gate compared against a budget the project already
   missed reported the same verdict before and after a 4.9× regression, and a paper-figure
   check asserting only that the right number appears *somewhere* in the text survived a
   deliberately wrong claim, because the same digits occurred two lines away. Both looked
   correct while reading. Neither was.
4. **On shared CI, gate a deterministic proxy.** Measure the noise floor first; substitute
   only when the substitution is 3 orders of magnitude quieter; print the real metric anyway.
5. **A revisit trigger fires silently unless something reads it.** Every one of our 68
   records states its own revisit condition, and one of them fired — build overhead — with
   the evidence sitting in the repository next to the record that named the threshold. Write
   the trigger *and* schedule the audit; the first without the second is a comment.
6. **Expect closure to break where a transport has an opinion.** Native dead-lettering,
   broker-specific ordering units, and partial fan-out are the three we hit. Budget for
   them; they are not surprises so much as a category.

---

## 9. Conclusion

Derivation closure is a small rule with a large consequence: if an address cannot be derived
from the compiled graph, it does not exist. Holding it required a second rule about how the
graph's published contract is allowed to grow, and a third about proving the gates. Over 68
decision records and 7 transports it held for most of the surface, broke in three places
that are documented at the point of failure, and cost **+67.1 %** of build time against a
**+8 %** budget that we have not met and have not renegotiated.

We think the honest summary is that the approach buys a real property — no second
declaration, therefore no drift, therefore renames that are complete and compatibility
reports that are total — at a build-time price that is currently too high, concentrated in
one component, and linear rather than super-linear in application size. Whether that trade
is worth making is a question about a reader's build, not about ours; what we can offer is
that the price is measured, the failures are named, and both are reproducible from the
repository.

---

## References

1. D. E. Perry and A. L. Wolf. *Foundations for the Study of Software Architecture.* ACM
   SIGSOFT Software Engineering Notes 17(4), 1992. — the erosion / drift distinction §2.1
   leans on.
2. R. Li, P. Liang, M. Soliman and P. Avgeriou. *Understanding software architecture erosion:
   A systematic mapping study.* Journal of Software: Evolution and Process 34(3), 2022.
   [doi:10.1002/smr.2423](https://onlinelibrary.wiley.com/doi/10.1002/smr.2423) ·
   [arXiv:2112.10934](https://arxiv.org/abs/2112.10934)
3. L. de Silva and D. Balasubramaniam. *Controlling software architecture erosion: A survey.*
   Journal of Systems and Software, 2012.
   [S0164121211002044](https://www.sciencedirect.com/science/article/abs/pii/S0164121211002044)
4. G. C. Murphy, D. Notkin and K. Sullivan. *Software Reflexion Models: Bridging the Gap
   between Source and High-Level Models.* FSE '95, ACM SIGSOFT Software Engineering Notes
   20(4), 1995. [doi:10.1145/222132.222136](https://dl.acm.org/doi/10.1145/222132.222136)
5. N. Ford, R. Parsons, P. Kua and P. Sadalage. *Building Evolutionary Architectures:
   Automated Software Governance.* O'Reilly, 2nd edition, ISBN 9781492097549. (1st edition
   2017, ISBN 9781491986363.)
6. Buf. *Detecting breaking changes.* <https://buf.build/docs/breaking/>
7. oasdiff. *OpenAPI Diff and Breaking Changes.* <https://github.com/oasdiff/oasdiff>

*Still to add before submission: the source-generated .NET mediator and compile-time DI
projects §7 concedes prior art to, cited by repository rather than by paper; Temporal or
Cadence for the durable-execution concession; and a citation for the mutation-testing
literature behind §5.5's kill-set argument.*

---

## Appendix A — how each number was obtained

Every figure in §5 traces to a command in this repository or to a committed record. Numbers
are re-read from these sources rather than copied between documents; carrying a number
between documents without re-reading it is how a wrong coverage figure survived several
phases here.

| Claim | Source |
|---|---|
| 68 ADRs | `ls docs/adr/ADR-*.md \| wc -l` |
| 67 Accepted / 1 Proposed | `PaperClaimTests`; note one record states its status inside a blockquote, so an anchored `^\*\*Status` grep undercounts by one |
| 68 / 68 with `Revisit when` | `rg -l 'Revisit when' docs/adr/ADR-*.md \| wc -l` |
| 88 fitness functions | `rg -c '\[Fact\]\|\[Theory\]' tests/FlowX.Architecture.Tests/*.cs --no-filename \| paste -sd+ \| bc`; case count from `dotnet test tests/FlowX.Architecture.Tests -c Release` |
| 48 diagnostic identifiers | `rg -o '"FLOWX1[0-9]{3}"' src/FlowX.Compiler \| cut -d: -f2 \| sort -u \| wc -l` |
| 7 adapters | `ls plugins/` |
| 75 schema field paths | walk every `properties` block of `schemas/flowx.manifest.schema.json` |
| 40 classification rules (22/9/9) | `rg -o 'DiffSeverity\.\w+' src/FlowX.Cli/Diffing/ManifestDiff.cs \| sort \| uniq -c` |
| 20 unproduced field paths of 80 declared | `schemas/unproduced-fields.json`, asserted against the schema and every emitted manifest by `ManifestProducerTests` |
| The refusal record | `docs/adr/ADR-0039-a-bus-subscription-publishes-no-new-manifest-field.md` |
| The field that closed, and the rule it had disabled | ADR-0017 §1 (struck row) |
| +67.1 % [61.9, 73.6] at 200 flows; +46.5 % [42.4, 51.0] at 50; A/A 10.6 % | `docs/benchmarks/B12-scale.md` §5.4 |
| `flows^0.91` [0.82, 1.08] | `docs/benchmarks/B12-scale.md` |
| Generator = 90.5 % of per-flow cost | `docs/benchmarks/B12-scale.md` §5.4 |
| 4.9× single-commit regression; 93 % type resolution; 20 % recovered | `docs/benchmarks/B12-scale.md` §5.2 |
| Wall clock ±139 % vs allocation ±0.069 % over 12 identical runs; +2 % threshold | `docs/benchmarks/generator-cost-gate.md` |
| The three closure failures | ADR-0073 (dead-letter), ADR-0072 (queue-as-partition), ADR-0053 (underivable tenant) |

**Not yet measured, and required before submission.** Two claims in this draft rest on
argument rather than data, and are marked here rather than dressed up:

1. **Drift avoided.** We assert that a second declaration drifts; we have not measured drift
   in a comparable system that has one. A defensible version needs either a controlled
   comparison or a mined history of address-configuration defects in a project of similar
   shape.
2. **The paper's own figures are gated, which is the artifact-evaluation claim.** Every
   corpus count above is pinned by `PaperClaimTests`: a pattern locates the claim in this
   file and its captured figure must equal what the repository yields. Cloning, building and
   running the suite is therefore the reproduction, and a disagreement is a red test naming
   both values. It caught three stale counts the first time it ran — one of them a miscount
   this paper had carried, because the grep it came from anchors `**Status:**` to the start of
   a line and one record states its status inside a blockquote. The gate's own first spelling
   was weaker than it looked and is described in §8, lesson 3.

3. **The completeness of D1's coverage.** §3.1's two gates assert that no transport option
   names an application concept and that every trigger kind has an emitter. Neither proves
   that the *set* of trigger kinds is the set of externally addressable surfaces — a surface
   reached by something that is not a trigger would satisfy both gates and violate D1. The
   remaining work is an enumeration of addressable surfaces independent of the trigger model.
