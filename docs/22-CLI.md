# 22 — The `flowx` Command-Line Tool

> **Status:** Accepted · **Audience:** engineers, release managers, CI authors
> **Answers:** what does the CLI do with the manifest, and what exactly makes a change "breaking"?

---

## 1. What the tool is for

`flowx` is a .NET tool that **reads published contracts and links no FlowX assembly.**
For four of its five verbs that means the manifest and nothing else; the fifth,
`replay`, also reads a journal. It has no project reference to any FlowX assembly,
deliberately: it consumes the manifest exactly as a third-party tool would, which is the
strongest available evidence that the document is genuinely self-describing rather than
only usable from inside this repository
([ADR-0005](adr/ADR-0005-manifest-as-build-artifact.md)). The architecture test
`CliLinksNoFlowXAssembly` holds that line, and still does —
[ADR-0020](adr/ADR-0020-cli-reads-the-journal-as-rows.md) widened the tool's *inputs*
without weakening its *links*, which is the distinction [§8](#8-flowx-replay-and-the-fitness-function--decided)
is about.

| Verb | Reads | Produces |
|---|---|---|
| `flowx graph` | a manifest | a Mermaid flowchart of every flow |
| `flowx manifest` | a built assembly | the manifest compiled into it |
| `flowx diff` | two manifests | a compatibility verdict, and a non-zero exit on a break |
| `flowx verify --cost` | a manifest | flows paying for an execution profile they do not use |
| `flowx replay --mode inspect` | a journal, and optionally a manifest | one instance's history, rendered ([§9](#9-flowx-replay---mode-inspect--reading-an-instance)) |

```
flowx graph    [--manifest <path>] [--flow <id>] [--output <path>]
flowx manifest  --assembly <path>  [--output <path>]
flowx diff      --old <path> --new <path> [--format text|json] [--output <path>]
flowx verify    --cost [--manifest <path>] [--format text|json] [--output <path>]
flowx replay    --instance <id> --mode inspect [--connection <string>] [--schema <name>]
               [--manifest <path>] [--format text|json] [--output <path>]
```

| Exit code | Meaning |
|---|---|
| 0 | success — the check found nothing |
| 1 | the check said no: `diff` found a breaking change, `verify` found something |
| 2 | usage error |
| 3 | the thing you named was not found: a file, or an instance |
| 4 | the store could not be reached |

`1` is distinct from `2` on purpose. A CI job has to tell "the gate says no" apart from
"the gate could not run": the first blocks a merge, the second is a broken pipeline, and
a tool that returns the same code for both gets the step deleted the first time somebody
mistypes a path.

**`4` is the same argument one step further out, and it was added for `replay`.** A
journal read has a third way to end that no file read has: the store may not answer.
Every existing code lies about that, and one of them lies dangerously — `3` would tell an
operator mid-incident that the instance does not exist when the truth is only that the
tool could not look, and "your data is gone" is the worst wrong answer available at three
in the morning. So it gets its own code. Nothing else in the tool can return it.

`flowx --help` lists exactly the table above and nothing else. It is not the place to
learn what is planned — §1.1 is — because a verb in a help text that exits `2` when you
type it is worse than a verb you never heard of.

### 1.1 Verbs other documents name, and this one does not have

**Five verbs. The table above is the whole tool.** Other documents in this set
invoke `flowx` with thirteen more, none of which is implemented. They are listed
here because this is the page a reader checks, and finding nothing said about a
verb they have just read elsewhere is worse than finding it listed as unbuilt.

| Verb | Named in | Blocked on |
|---|---|---|
| `flowx replay --mode simulate \| resume \| fork` | [12](12-Observability.md), [20](20-Roadmap.md) | **each needs the execution engine**, which is the one thing the CLI may not link. `--mode inspect` is built (§9) and is the only mode that runs nothing; [ADR-0020 §5](adr/ADR-0020-cli-reads-the-journal-as-rows.md)#5-what-this-record-does-not-decide) is explicit that its argument does **not** reach these three. They are blocked on a decision, not only on code |
| `flowx query`, `flowx ai …`, `flowx generate` | [13](13-AI-Native.md), [15](15-Security.md), [19](19-SDK.md) | **P8** |
| `flowx dev`, `flowx new`, `flowx run`, `flowx docs`, `flowx bench`, `flowx fill` | [19](19-SDK.md) | no template or dev-loop tooling exists. For benchmarks use `scripts/run-benchmarks.sh` |
| `flowx cancel`, `flowx signal` | [06](06-Execution-Engine.md) | **P2** — there is no durable instance to cancel or signal |
| `flowx tenant`, `flowx purge` | [15](15-Security.md), [16](16-Multi-Tenant.md) | **P6** |

`flowx verify` was named with three checks and now has one. The other two are not
delayed, they are answered elsewhere or unanswerable:

| Check | Named in | Status |
|---|---|---|
| `--cost` | [ADR-0003](adr/ADR-0003-execution-profiles.md), [18](18-Cloud-Native.md) | **built** — §7 |
| `--complete` | [01](01-Vision.md), [03](03-Design-Principles.md), [13](13-AI-Native.md), [ADR-0005](adr/ADR-0005-manifest-as-build-artifact.md) | **superseded.** `ManifestIsComplete` does the job, and does it *in* the build rather than after it. A CLI verb would be a second implementation of one rule, run later, and reachable only by a pipeline that remembered to call it |
| `--runtime` | [05](05-Architecture.md) (R7), [ADR-0014](adr/ADR-0014-derived-error-catalogue-vs-build-budget.md) | **not buildable yet.** It compares the built manifest against the deployed one, and nothing records what is deployed. It needs a control plane, which no phase currently owns |

`--complete` and `--runtime` are rejected **by name**, not as unknown options, and the
error says where each check went. Somebody who read `--complete` in
[03](03-Design-Principles.md) and typed it has done nothing wrong; `unknown option` would
send them looking for a typo they did not make.

---

## 2. `flowx diff` — the gate

```bash
flowx diff --old baseline/flowx.manifest.json --new artifacts/flowx.manifest.json
```

The baseline is the last released manifest — published alongside the artifact it
describes, per ADR-0005. The candidate is what this build produced. Every difference is
classified as **breaking**, **additive** or **neutral**, and a single breaking finding
exits 1.

### 2.1 The compatibility unit is `id@major`

Flows pin the major version of every capability they compiled against
([07 §5](07-Capability-Model.md)), and a capability may legitimately exist at two majors
side by side. So the diff groups flows, capabilities and events by `id@major` and
compares the newest entry within each group.

Three consequences fall out of that one decision, and all three are what people expect:

- Publishing `payment.capture@3.0.0` **alongside** `2.1.0` is purely additive.
- Publishing `3.0.0` **instead of** `2.1.0` is a removal of the `@2` contract, and
  breaking — which is exactly what it is for a flow pinned to `@2`.
- A patch or minor bump within a major is not a change at all, because a minor bump is
  compatible by definition; if it was not, the contract change itself is reported.

It also means there is no "was the version bumped?" waiver anywhere in the tool. Bumping
the major *is* publishing a new contract; deleting the old one is what breaks people.

### 2.2 What is never reported

A rule that fires on every build is worse than no rule, because it teaches people to
ignore the output.

| Ignored | Why |
|---|---|
| `source` (file:line) | moves whenever anyone edits above a declaration; it is navigation metadata |
| `application.version`, `commit`, `builtAt` | they change on every release by design |
| a flow's `steps`, **except** an `AwaitSignal` step's `signal` and `timeout` | every other step describes what the flow *does*, and refactoring that is what FlowX exists to make safe. A wait describes what the flow **requires from outside** — the identity a sender addresses to continue it — which is the same kind of fact as a `trigger` and is compared for the same reason ([ADR-0021 §2.4](adr/ADR-0021-manifest-publishes-the-wait.md)). Moving a wait behind a `When`, or changing the steps around it, still reports nothing |
| a flow's `emits` and `errors` | both are aggregated by the compiler from steps and capabilities; the same facts appear once more, with versions, in `events` and each capability's `errors` |
| array order anywhere | every list is compared as a set |

---

## 3. The classification rules

### 3.1 Breaking

| Code | Change | Why it breaks somebody |
|---|---|---|
| `FLOWX-DIFF-001` | flow removed | every trigger bound to it stops resolving |
| `FLOWX-DIFF-002` | flow input contract changed | callers construct that type; the same request no longer binds |
| `FLOWX-DIFF-003` | flow output contract changed | callers deserialise it; the response no longer round-trips |
| `FLOWX-DIFF-004` | `Durable` profile withdrawn | instances stop surviving a process kill and in-flight work is lost — with no signature change to catch it |
| `FLOWX-DIFF-005` | trigger removed | a route stops answering, or a consumer group stops draining a topic producers keep filling |
| `FLOWX-DIFF-006` | member no longer `sensitive` | a value that was redacted now reaches logs, traces and the journal, for the whole retention window |
| `FLOWX-DIFF-007` | idempotency key became required | requests without the key are rejected at admission, before the flow exists — every caller that does not send one starts failing |
| `FLOWX-DIFF-008` | idempotency key no longer required | deduplication is withdrawn: a caller's retry after a timeout executes the flow a second time instead of returning the recorded result. **Nothing fails; the work simply happens twice**, which is why this direction is breaking too |
| `FLOWX-DIFF-009` | agent confirmation weakened | a model that had to ask a human before invoking this flow now invokes it, and nothing else in the build notices |
| `FLOWX-DIFF-010` | capability removed | flows pinned to that major no longer resolve |
| `FLOWX-DIFF-011` | capability input contract changed within a major | within a major the contract is frozen; nobody can detect this from the version |
| `FLOWX-DIFF-012` | capability output contract changed within a major | as above |
| `FLOWX-DIFF-013` | `idempotent: true → false` | retry policies already attached at call sites become a compile error (`FLOWX1014`), in other repositories |
| `FLOWX-DIFF-014` | authorisation relaxed | **security regression** — reachable by principals the baseline refused, and nothing else in the build notices |
| `FLOWX-DIFF-015` | authorisation tightened, or the named permission changed | callers authorised under the baseline are now denied |
| `FLOWX-DIFF-016` | side effect added | blast radius widened; agent confirmation prompts and every impact assessment made against the baseline are now wrong |
| `FLOWX-DIFF-017` | error code removed | consumers branching on it silently stop matching — codes disappear far more often because they were renamed |
| `FLOWX-DIFF-018` | error category changed | the category drives the transport status code, so a client keyed on 409 now sees 403 for the same failure |
| `FLOWX-DIFF-020` | event removed, or its schema major bumped without keeping the old one | subscribers pinned to that major receive nothing, and nothing in their build says so |
| `FLOWX-DIFF-021` | a flow no longer waits for a signal it waited for | the quietest break here. A delivery to an instance that is not waiting for that signal is **inert, not refused** — so senders keep posting, every delivery is accepted, nothing fails, nothing is logged, and any instance expecting it waits until its deadline |
| `FLOWX-DIFF-022` | a flow waits for a signal it did not wait for | the flow stops finishing on the request that starts it. Over HTTP the answer changes from the flow's output to a `202` carrying an instance id, and the work does not complete until somebody delivers a signal the baseline never published |

### 3.2 Additive

| Code | Change |
|---|---|
| `FLOWX-DIFF-100` | flow added |
| `FLOWX-DIFF-101` | capability added |
| `FLOWX-DIFF-102` | event added |
| `FLOWX-DIFF-103` | trigger added |
| `FLOWX-DIFF-104` | error code added |
| `FLOWX-DIFF-105` | `idempotent: false → true` |
| `FLOWX-DIFF-106` | side effect removed |
| `FLOWX-DIFF-107` | member marked `sensitive` |
| `FLOWX-DIFF-108` | agent confirmation strengthened — a human is asked in more cases than before |

### 3.3 Neutral — reported, never gated

| Code | Change | Why it is not a break |
|---|---|---|
| `FLOWX-DIFF-200` | manifest schema major differs | says how to read the document, not what the application promises — but the rest of the report is then advisory |
| `FLOWX-DIFF-201` | application renamed | almost always the wrong pair of files, which is worth saying before a reader trusts a hundred findings |
| `FLOWX-DIFF-202` | execution profile changed other than losing `Durable` | cost and delivery semantics change; the contract does not |
| `FLOWX-DIFF-203` | deadline changed | an operational budget tuned against production latency, not a promise — though shortening one can turn slow-but-successful executions into timeouts |
| `FLOWX-DIFF-204` | deprecation notice added or removed | nothing breaks today; it is the signal to start migrating |
| `FLOWX-DIFF-205` | schedule time zone changed | the schedule fires at a different wall-clock time, and its DST behaviour changes with it — operationally significant, contractually nothing |
| `FLOWX-DIFF-206` | the wait a flow declared for a signal changed | an operational budget tuned against how long real people take, not a term of the contract — and **nothing arms it**, because there is no scheduler and no timer table ([06 §6](06-Execution-Engine.md#6-suspension-waiting-without-holding-resources)). A change into or out of `(none)` means the compiler's ability to evaluate the declaration moved, which is a fact about the build. [ADR-0021](adr/ADR-0021-manifest-publishes-the-wait.md) records that this severity is re-argued the day a timer fires |
| `FLOWX-DIFF-207` | the flows that emit an event changed | a subscriber handles the event, not its producer: gaining one changes nothing it can see, and losing one matters only when it was the last — which is the event leaving the catalogue and is `FLOWX-DIFF-020`. Reported because "who do I talk to when this event is wrong" is answered by this list and by nothing else in the document |
| `FLOWX-DIFF-019` | **one side's error catalogue is withheld, so the two were not compared** | the compiler could not resolve a catalogue, which is a fact about the *build* and not about the contract. It sits out of numeric order because it belongs to the 01x error-catalogue family and to this severity |

**`FLOWX-DIFF-019` exists because its absence was worse than a false negative.**
`ManifestDocument.Errors` used to default to an empty list, which collapsed *"the compiler
withheld this catalogue"* into *"this capability declares no errors"* — so a build that
merely stopped resolving a catalogue reported every code as removed, `FLOWX-DIFF-017`,
**Breaking**, in a gate that blocks merges. The fix was to make withheld nullable and skip
the comparison; this code is what stops the skip being indistinguishable from "no changes".

*It shipped emitting this code and documenting it nowhere, and it was not alone.* Until
2026-07-31 **six** of the codes `flowx diff` emits had no row in this document:
`FLOWX-DIFF-007`, `008`, `009` (all **Breaking**), `108`, `205` and `019`. Three of them
block a merge. A reader who hit one in CI output had no page to look it up in — the state
`docs/diagnostics/README.md` forbids for the `FLOWX1xxx` family, never checked for this one
because the two families are governed by different files and only one of them had a rule.

**`ManifestDiffCodesAreDocumented` now closes the set in both directions**, so a code that
is emitted and unlisted fails the build, and so does a row here describing a code nothing
emits. It was written after the gap was found by hand, which is the wrong order and is
recorded as such.

---

## 4. Three rules worth arguing about

### 4.1 Both directions of an authorisation change are breaking

Relaxing is a security regression and is labelled as one, in those words, because
"authorization changed" in a CI log does not make anybody stop reading.

Tightening is breaking too, which is the less comfortable half. It is unambiguously the
right change to make, and it still stops callers that worked yesterday from working
today. The gate is not saying tightening is wrong; it is saying tightening needs the same
coordination as any other break, because shipping it unannounced turns a security
improvement into an outage. It gets its own code so the two never read as the same event.

`Permission` and `Policy` are not ordered against each other — a named policy can be
broader or narrower than a named permission — so a move between them is reported as a
change rather than guessed at in either direction.

### 4.2 `sensitive` is asymmetric, on purpose

**Marking** a member sensitive is additive. It strictly increases protection. It can
break something — a dashboard scraping the value out of a log line loses it — but a
secret leaking into a log is not a dependency this platform undertakes to preserve, and a
gate that failed the build when an engineer marked a password would teach engineers not
to mark passwords. That is the opposite of the intended effect.

**Un-marking** one is breaking, and it is the more serious of the two. The value now
flows into logs, traces and a journal retained for the replay window, so the regression is
durable and applies to every instance recorded after the change. It is the same class of
finding as a relaxed authorisation stance: no signature moved, the security posture got
worse, and that is precisely what code review misses.

### 4.3 Adding a side effect is breaking

Nothing about the call changes, which makes this the least obvious rule. But
`sideEffects` is what blast-radius review reads and what decides whether an agent asks a
human before invoking the tool ([13 §6](13-AI-Native.md)). A capability that consumers
assessed as touching nothing outside the process, and which now writes to a payment
gateway, has invalidated every assessment made against it. Removing one narrows the blast
radius and invalidates no decision anyone made, so it is additive.

---

## 5. Output

Text is the default, because the overwhelmingly common reader is a person scrolling a CI
log after a build went red.

```
flowx diff — Ecommerce: 1.0.0 -> 1.1.0

BREAKING (2)
  FLOWX-DIFF-006  flow order.place@1 input
      member is no longer sensitive: PaymentToken
      Data exposure regression: this value was redacted from logs, traces and the
      durable journal, and now reaches all three for the whole retention window.
  FLOWX-DIFF-014  capability payment.capture@2
      authorization relaxed: Permission -> Public
      Security regression: the capability is now reachable by principals the baseline
      refused. Nothing else in the build will notice this.

NEUTRAL (1)
  FLOWX-DIFF-203  flow order.place@1
      deadline changed: PT30S -> PT10S
      A shorter budget can turn slow-but-successful executions into timeouts.

2 breaking changes, 0 additive, 1 neutral — INCOMPATIBLE.
```

`--format json` produces the same verdict for whatever consumes the build afterwards.
Both renderings come from one report rather than two traversals of the manifests, so the
gate and the log cannot disagree about what was found.

```jsonc
{
  "application": "Ecommerce",
  "baselineVersion": "1.0.0", "candidateVersion": "1.1.0",
  "compatible": false,
  "breaking": 2, "additive": 0, "neutral": 1,
  "findings": [
    { "code": "FLOWX-DIFF-014", "severity": "Breaking",
      "subject": "capability payment.capture@2",
      "summary": "authorization relaxed: Permission -> Public",
      "consequence": "Security regression: …" }
  ]
}
```

The verdict and the counts come before the findings, so a consumer that only needs the
gate's answer reads the first few hundred bytes rather than the whole document.

---

## 6. Using it in CI

```bash
flowx manifest --assembly artifacts/App.dll --output new.manifest.json
flowx diff --old baseline/flowx.manifest.json --new new.manifest.json
```

No `if` and no wrapper: a breaking change exits 1 and fails the step. A pipeline that
wants the machine-readable form as well can write it out and still get the verdict from
`$?`, because `--output` does not change the exit code.

Waiving a finding is deliberately not a flag. A breaking change ships behind a major
version bump or an ADR ([15 §9](15-Security.md), change control), and a `--ignore` option
would turn both of those into an argument in a YAML file.

---

## 7. `flowx verify --cost` — the profile chosen by accident

```bash
flowx verify --cost --manifest artifacts/flowx.manifest.json
```

[ADR-0003](adr/ADR-0003-execution-profiles.md) makes durability opt-in and then names the
one thing that decision leaves open: nothing stops somebody opting in by accident.
[18 §Cost](18-Cloud-Native.md) puts the number on it — a read-heavy flow mistakenly marked
`Durable` can cost 100× its `Ephemeral` equivalent in storage and IOPS for zero benefit,
and it is the single largest cost lever in the platform.

Both documents describe the same detector, in the same words, and this is it:

> a durable flow with **no compensation, no signals and no timers**.

| Code | Fires on |
|---|---|
| `FLOWX-VERIFY-001` | a flow whose `profile` is `Durable`, in which no step registers a `compensation`, and no step is an `AwaitSignal` or a `Delay` |

Nothing else is a rule. The check has exactly the one the ADR specified, and inventing a
second — "durable and under three steps", "durable with an HTTP trigger" — would be this
tool asserting a cost model nobody agreed to.

### 7.1 What clears a flow

A flow is cleared by **any** compensation, signal or timer, wherever it appears:

- **Nested in a branch.** A saga that compensates inside a `When` arm, or awaits a signal
  in one case of a `Switch`, is using durability exactly as designed. Reading only the top
  level would report every conditional saga there is.
- **Inside an inline sub-flow.** An `Inline` child's steps run within the parent's
  execution and share its budget, so the child's compensations are the parent's. A
  `Detached` child has its own deadline, lifecycle and profile — what it does is no
  argument for the parent being durable, and it is not counted. `AwaitCompletion` suspends
  the parent outright, which is durability by itself.

### 7.2 It abstains rather than guessing

When a flow composes an inline sub-flow that **this manifest does not describe** — the
child was compiled into another assembly, so the document names it and stops — the check
says nothing about the parent.

That is deliberate and it is the asymmetry the whole check rests on. A missed finding
costs storage. A false one costs the rule: a cost gate that accuses a correct saga is a
cost gate somebody deletes from the pipeline, and then it catches nothing at all. The
same reasoning is why [§2.2](#22-what-is-never-reported) exists for `diff`.

### 7.3 Reading it

```
flowx verify --cost — Ecommerce 1.1.0

COST (1)
  FLOWX-VERIFY-001  flow report.daily@1.0.0
      profile is Durable, with no compensation, no signal and no timer
      It pays for a journal write per step, a lease and a resumption path, and uses
      none of the three.

1 of 2 durable flows uses nothing durability provides.
```

The denominator is published, in text and in JSON, because "no findings" over an unknown
total is also exactly what reading the wrong file looks like. A manifest with no durable
flow at all says so in as many words rather than printing nothing.

```jsonc
{
  "application": "Ecommerce", "version": "1.1.0",
  "passed": false, "durableFlows": 2, "flagged": 1,
  "findings": [ { "code": "FLOWX-VERIFY-001", "subject": "flow report.daily@1.0.0", … } ]
}
```

### 7.4 `--cost` is required, not defaulted

`flowx verify` on its own is a usage error. The verb was documented with three checks, so
a bare invocation is far likelier to mean "I read about one of the other two" than "run
whatever you have" — and guessing would run a check the caller did not ask for and then
report a clean result for the one they did.

Exit `1` on a finding, like `diff`, so a pipeline that wants this gating gets it with no
wrapper and one that wants it advisory runs it in a step allowed to fail. There is no
`--ignore`, for the reason [§6](#6-using-it-in-ci) gives.

### 7.5 Why it belongs in the CLI

Every input the rule needs is already published: the profile, each step's kind, and the
compensation registered against a step. So the check costs no new contract, needs no
assembly and keeps `CliLinksNoFlowXAssembly` green.

It also reads `Delay` — a step kind `flowx.manifest.schema.json` defines and this
repository's generator has no case for yet. That is the correct way round. The CLI is a
consumer of the schema, not of the current emitter, so the day `Delay` is emitted this
check is already right.

---

## 8. `flowx replay` and the fitness function — decided

> This section used to record a *finding* and say that closing it was not this page's
> call. It was closed by [ADR-0020](adr/ADR-0020-cli-reads-the-journal-as-rows.md), and
> what follows is the outcome rather than the argument — the record carries that.

**Neither of the two resolutions this section sketched was taken whole**, and the reason
is that the audit it recorded was right about the rule and wrong about the journal.

Right about the rule: `CliLinksNoFlowXAssembly` asserts that `FlowX.Cli.csproj`
has no `ProjectReference`, and it counts links rather than inputs. Reading a journal as
**data** leaves it green untouched. So no amendment was forced, and none was made — the
test stands exactly as written. `Npgsql` is a `PackageReference`, the same kind of
dependency `System.Reflection.MetadataLoadContext` already was.

That test was called `CliDependsOnNothingButTheManifest` until the rename ADR-0020's
owed-work list asked for. The old name claimed more than the assertion checked — it named
one input for a tool that has always read two, since `flowx manifest --assembly` reads a
built assembly — and that gap is what let a *link* rule be reported as colliding with a new
*input*. The assertion never moved.

Wrong about the journal: publishing a `flowx.journal.schema.json` was the other half of
the sketch, and it does not fit what a journal is. A JSON Schema describes a *document*,
and **nothing writes a journal document** — there is no file for the schema to describe.
Worse, it would be a stability promise about data at rest, made over a shape that is still
moving: `plugins/FlowX.Postgres/Migrations` is on its fourth migration and
[ADR-0016](adr/ADR-0016-postgres-journal-adapter.md) has amended the row shape twice. The
manifest can carry [ADR-0017](adr/ADR-0017-manifest-v1-freeze-criteria.md)'s freeze plan
because it is rebuilt from source on every build; a journal holds rows written by every
version that ever ran.

**So the journal's contract is its DDL**, which is already published for exactly this kind
of reader — the migrations ship as reviewable SQL rather than as C# string constants so a
DBA can read them without a .NET toolchain. `replay` reads rows over that, and joins them
against the plan the manifest publishes, which is the part the audit got right and which
is what makes the verb possible at all.

Three things changed as a result, and one thing deliberately did not:

| | |
|---|---|
| §1's prose | widened. "Reads the manifest and nothing else" was already false of `flowx manifest --assembly`; it is now stated as *reads published contracts and links no FlowX assembly* |
| Exit codes | gained `4`, because a store read can fail in a way a file read cannot |
| A new assertion | `EveryVerbButReplayRunsWithNoStore`. "The CLI runs against an artifact with no database" was one of three properties resting on the rule's *name* and the only one it never asserted — true because no verb had needed a store. It is checked now |
| The fitness function | **unchanged in body, renamed since**. Its name overclaimed and WP-64 put it one step further from what it asserts; the rename to `CliLinksNoFlowXAssembly` was recorded as owed work in ADR-0020 rather than smuggled in on this feature branch, and was carried out on 2026-08-01 together with the six documents citing it |

**What is not decided.** `simulate`, `resume` and `fork` all execute, and ADR-0020's
argument reaches none of them — reading rows needs no engine and that is the whole of why
`inspect` is legal. §1.1 lists them as blocked on a decision, which is more honest than
listing them as blocked on a phase.

---

## 9. `flowx replay --mode inspect` — reading an instance

```bash
flowx replay --instance 0198f3a1-6c2e-7b41-9f0d-2a5c8e4b1d33 --mode inspect \
             --connection "$FLOWX_POSTGRES_CONNECTION" --manifest flowx.manifest.json
```

```
Flow order.place@1.2.0   instance 0198f3a1-6c2e-7b41-9f0d-2a5c8e4b1d33   tenant acme
State: CompensationFailed   Duration: 4.21s   Started: 2026-07-30T09:14:00.001Z
Correlation: req-8f21c4a0
Input: unknown — flow_instance.input is NULL, which does not distinguish
       a flow started with no input from one whose input was never captured.

  ok    step 0        order.validate@1.0.0               2ms   -> {"id":"ord_7741",…}
  ok    step 1        screen.sanctions@1.0.0            12ms   -> {"cleared":true}
  ok    step 2        screen.fraud@1.0.0                31ms   -> {"score":0.02}   [capture may be a sibling's — see caveats]
  ok    step 4[0]     inventory.reserve@1.0.0           41ms   -> {"sku":"SKU-1","qty":2}
  ok    step 4[1]     inventory.reserve@1.0.0           38ms   -> {"sku":"SKU-9","qty":1}
  FAIL  step 5        payment.capture@2.1.0            1.61s   (attempt 1)   -> {"code":"payment.gateway_timeout",…}
  FAIL  step 5        payment.capture@2.1.0            1.98s   (attempt 2)   -> {"code":"payment.gateway_timeout",…}
  comp  step 6[0]     inventory.release@1.0.0          493ms   -> {"code":"inventory.unavailable",…}

  Non-deterministic values captured:
      step 0        {"utcNow":"2026-07-30T09:14:00.001+00:00"}
      step 2        {"newIds":["0198f3a1-7000-…","0198f3a1-7001-…"]}   [inside a fork]

8 steps in commit order.

Caveats:
  - flow_instance.input is NULL for this instance, so the flow's input is not shown. …
  - step 1, step 2 are branches of a Parallel. A fork's branches share one execution context, …
  - A compensation's ambient reads are captured by nothing, …
```

### 9.1 The caveats are the point

An `inspect` output is read during an incident, by someone who is in no position to go and
check it. So every way the journal is **known to be less than it looks** is stated in the
output rather than left for the reader to know already. Each caveat below is pinned by a
test somewhere else in this repository, and none of them is a disclaimer.

| Caveat | Why it is there |
|---|---|
| `Input: unknown` | `flow_instance.input` is NULL on every row ever written — `FlowHost` passes `input: null`. NULL cannot be told apart from a flow that genuinely started with none, so the tool refuses to guess. Rendering `{}` would be a positive claim about what the flow received |
| Fork attribution | A `Parallel`'s branches share one execution context, so `TakeNondeterminism` at a branch's commit takes everything minted since the last commit — **including a sibling's**. `ReplayDeterminismTests.AForkAttributesOneBranchsCapturedIdToItsSiblingsRow` measures it. The journal cannot show this: both rows look ordinary. Only the plan says which steps are branches, which is what the manifest join is for |
| No manifest | Without the plan the fork check *cannot run*, so the output says so. Silence would read as "it ran and found nothing", which is the more damaging of the two |
| Compensation captures | A compensation's ambient reads are captured by nothing (ADR-0015), so what appears under a `comp` row is the forward step's capture |

**The fork caveat is scoped, and the scoping is the whole value of the manifest join.** A
warning that appears on every history is one a reader learns to skip; `step 1, step 2 are
branches of a Parallel` is one they can act on.

### 9.2 Two departures from [12 §5](12-Observability.md#5-flow-replay--the-differentiator)

That page's worked output is the spec for this one's shape, and it is followed except here.

**Markers are ASCII, not emoji.** `ok` / `FAIL` / `comp` rather than the document's
✅ / ❌ / 🔄. This output goes into CI logs and redirected files at least as often as into
a terminal; emoji are double-width on some terminals and single on others, which breaks the
column alignment that makes a history scannable — and `grep FAIL` is a thing an operator
can type at three in the morning. The tool also builds with `InvariantGlobalization`.

**There is no `Trigger:` line.** 12 §5 shows `Trigger: kafka:orders.requested[3]@1042`, and
**nothing journals a trigger.** `flow_instance` carries `correlation_id` and `trace_id`, and
neither is the broker, topic, partition or offset that line describes. Printing a plausible
one would be the same failure as rendering `Input: {}`. The correlation id is shown instead,
because it is what the store actually holds.

A third, smaller difference is an improvement rather than a departure: 12 §5 labels an
attempt only when it is not the first, which leaves a retried step looking like one
unlabelled row and one labelled one. Here **every** row of a step that was attempted more
than once is labelled, and a step attempted exactly once is not labelled at all — the
interesting fact is the retry, not the ordinal.

### 9.3 What it costs, stated because ADR-0020 accepts it

`Replay/JournalReader.cs` names columns that `0001_initial_schema.sql` defines, and **no
compiler check connects the two files.** A column rename breaks this verb at run time. The
only thing that catches it is `ReplayInspectTests`, which runs the verb against a migrated
schema — and which **fails rather than skips** when a connection string is set and the
server does not answer, because a skip there would leave the accepted cost with no
compensating control at all.

---

**Back to:** [README](../README.md) · [AI-Native](13-AI-Native.md) · [Capability Model](07-Capability-Model.md) · [ADR-0005](adr/ADR-0005-manifest-as-build-artifact.md) · [ADR-0020](adr/ADR-0020-cli-reads-the-journal-as-rows.md)
