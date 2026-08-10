# Sample — A CRM with a process an administrator can change

**Claim proved:** the transitions, guards and actions of a sales process live in tables, an
administrator rewrites them at run time, and the behaviour changes **with no rebuild and no
deployment** — while the set of things the process can *do* stays closed at compile time.

The design this is built from is [docs/26 — CRM Sample](../../docs/26-CRM-Sample.md): the C4
views, the class and database diagrams, the sequences and the twelve-package plan.

Run it:

```bash
FLOWX_POSTGRES_CONNECTION="Host=localhost;Port=5432;Database=postgres;Username=postgres" \
  dotnet run --project samples/crm
```

It needs PostgreSQL. A broker is optional and what it costs to leave one out is stated rather
than hidden: the configured process still runs, because it is driven by the change feed over the
outbox, and the three subscriptions on `lead.created` do not, because a `[BusTrigger]` with no
consumer has nothing to read. Add one and they do:

```bash
FLOWX_RABBITMQ_CONNECTION="amqp://guest:guest@localhost:5672/" \
FLOWX_POSTGRES_CONNECTION="..." dotnet run --project samples/crm
```

Or the whole stack — database, API, web client — with `cp .env.example .env` and
`docker compose up --build` from the repository root. Only the client publishes a port.

Two probes, because they answer two questions: `/health/live` runs no checks and says only that
the process is worth keeping, and `/health/ready` says whether the schema behind it is the one
this build writes against. `/health` is readiness, as it always was. A container probes itself
with `dotnet Crm.dll --healthcheck` — the runtime image has no shell to probe it with.

A tenant starts empty. `CRM_SEED_FILE` names a JSON document holding a tenant's metadata and its
rows — custom objects and fields, a configured process with its stages, and accounts, contacts,
opportunities and leads. Each item carries an alias, the row's id is derived from it, and
applying the same file again therefore writes nothing: `samples/crm/seed/northwind.json` is the
one compose mounts.

**And it refuses to run in a Production environment unless you say you meant it.** There is no
`launchSettings.json` here, so `dotnet run` starts in Production, and `CRM_SEED_FILE` alone then
throws at start-up rather than writing rows that would be indistinguishable from real ones. Say
which you mean:

```bash
CRM_SEED_FILE=samples/crm/seed/northwind.json CRM_SEED_ALLOW_PRODUCTION=true dotnet run   # or
CRM_SEED_FILE=samples/crm/seed/northwind.json DOTNET_ENVIRONMENT=Development dotnet run
```

The seeder logs one line per tenant — `Seeded tenant crm-northwind … N written, M already there.`
A healthy `/health` does **not** imply the seed landed; the browser suite in `.github/workflows/ci.yml`
spent thirty minutes discovering that before it started checking for that line.

## What it is

Sixty-four tables, eighty-three flows and three authorisation stances, over the entities a CRM actually
has: leads, accounts, contacts, opportunities, quotes, orders, tasks and a configurable process.

| Surface | Route or trigger | What it demonstrates |
|---|---|---|
| Capture a lead | `POST /api/v1/crm/leads` | one write and one event, staged in the same transaction |
| Score, assign, enrich | `lead.created` ×3 | three subscriptions, one publish, independent redelivery |
| Convert a lead | `POST /api/v1/crm/lead-conversions` | a saga whose compensations take the step's **input** |
| Quote | `POST /api/v1/crm/quotes` | pure pricing; the discount threshold decides Draft or Issued |
| Approve a discount | `POST /api/v1/crm/quotes/approvals` | `crm.discount.approve` — a manager holds it, a representative does not |
| Order | `POST /api/v1/crm/orders` | the threshold asked again, where the money is committed |
| Advance an opportunity | `POST /api/v1/crm/opportunities/triggers` | the seam: it announces, the configured process decides |
| Create a task | `POST /api/v1/crm/tasks` | a polymorphic reference held up by a trigger, not a foreign key |
| Escalation sweep | `[CronTrigger("0 * * * *")]` | one statement; an overdue task escalates once per window |
| Stale sweep | `[CronTrigger("0 6 * * *")]` | counts, and leaves what to do about it to the process |
| Summarise an account | `POST /api/v1/crm/account-summaries` + `[AgentTrigger]` | one stance, two transports |
| Schema probe | `POST /api/v1/crm/schema-probes` | row-level security, demonstrated over HTTP |
| Declare an object | `POST /api/v1/crm/custom/objects` | an entity this build has never heard of, at run time |
| Declare a field | `POST /api/v1/crm/custom/fields` | a column on a built-in entity or on a custom object |
| Declare a relationship | `POST /api/v1/crm/custom/relationships` | a named edge, with a cardinality the database keeps |
| Write a record | `POST /api/v1/crm/custom/records` | `crm.write` — declaring the shape is a different grant |
| Link two records | `POST /api/v1/crm/custom/links` | the cardinality asked where the link is made |
| Set custom fields | `POST /api/v1/crm/custom/entity-fields` | a merge, so two clients editing different fields do not collide |
| Register a connector | `POST /api/v1/crm/connectors` | `crm.admin` — an address this server will later send to |
| Enable a connector | `POST /api/v1/crm/connectors/enablement` | off without losing what it already sent |
| Publish to a connector | `POST /api/v1/crm/connectors/deliveries` | queued, not sent; the response says `Pending` and means it |
| Delivery sweep | `[CronTrigger("* * * * *")]` | `FOR UPDATE SKIP LOCKED`, so two replicas do not double-send |
| Declare a validation rule | `POST /api/v1/crm/custom/validation-rules` | refuses when it holds, in the administrator's own words |
| Declare a roll-up | `POST /api/v1/crm/custom/rollups` | an aggregate over a parent's children, recomputed on link |
| Save a list view | `POST /api/v1/crm/custom/list-views` | a named query, its fields checked when it is saved |
| Query records | `POST /api/v1/crm/custom/queries` | the one projection of custom values, and where reads are masked |
| Search everything | `POST /api/v1/crm/search` | one statement over five tables; a hit is an identity, not a row |
| Read the process | `POST /api/v1/crm/processes` | the stages, guards and actions an administrator published, with how many deals sit in each |
| Read one plan | `POST /api/v1/crm/planning/plan` | objectives, steps, risks, qualification and stakeholders in one read; overdue is the server's answer |
| List what is declared | `POST /api/v1/crm/config` | twelve tables, one route; each row carries a sentence saying what it does |
| Page an entity | `POST /api/v1/crm/entities` | a filter of unknown size, bound as three arrays rather than built; what may be read is a superset of what may be declared on |
| Declare a formula | `POST /api/v1/crm/custom/formulas` | computed from the same record, one operation, no nesting |
| Describe the schema | `POST /api/v1/crm/describe` | what a client renders from; permissions resolved, not reported as rules |
| Sync what changed | `POST /api/v1/crm/custom/changes` | tombstones included, and a cursor that is not a clock |
| Delete a record | `POST /api/v1/crm/custom/record-deletions` | leaves a tombstone, so an offline client learns it is gone |
| Bulk import | `POST /api/v1/crm/bulk/imports` | queued; a bad row is reported by position and the rest still land |
| Bulk export | `POST /api/v1/crm/bulk/exports` | redacted for who submitted it, never for who ran it |
| Job status | `POST /api/v1/crm/bulk/jobs` | durable progress, so a resumed job continues rather than restarts |
| Job sweep | `[CronTrigger("* * * * *")]` | one chunk per pass, with derived ids so a re-run writes no duplicates |
| Build a report | `POST /api/v1/crm/reports` | a closed vocabulary, so the dimension is a bound value and not SQL |
| Run a report | `POST /api/v1/crm/reports/runs` | `crm.read` — building one is administrative, reading it is not |
| Build a dashboard | `POST /api/v1/crm/dashboards` | tiles name reports; deleting a report in use is refused |
| Run a dashboard | `POST /api/v1/crm/dashboards/runs` | every tile in one request, not twelve round trips |
| Rename anything | `POST /api/v1/crm/labels` | the label moves, the identifier never does |
| Declare a period | `POST /api/v1/crm/planning/periods` | a quarter that sticks out of its year is refused |
| Set the number | `POST /api/v1/crm/planning/strategies` | `crm.admin` — one per period, and the vision beside it |
| Commit a plan | `POST /api/v1/crm/planning/plans` | account, deal or demand; each carries only what its kind needs |
| Qualify a deal | `POST /api/v1/crm/planning/qualifications` | eight elements, answered or not — never a self-scored rating |
| Agree a step | `POST /api/v1/crm/planning/steps` | the mutual action plan; an overdue step is the earliest signal |
| Roll a period up | `POST /api/v1/crm/planning/roll-ups` | target, committed, and the gap — scoped by who is asking |
| Place somebody | `POST /api/v1/crm/org/members` | the reporting line; a placement that would loop it is refused |
| Set an objective | `POST /api/v1/crm/planning/objectives` | what an account plan is for, beyond a number |
| Map a stakeholder | `POST /api/v1/crm/planning/stakeholders` | who has not agreed yet, which is what loses B2B deals |
| Raise a risk | `POST /api/v1/crm/planning/risks` | closed ones stay on the register as evidence |
| Declare a KPI | `POST /api/v1/crm/kpis` | a source, a target and a direction — never a stored figure |
| Read the scorecard | `POST /api/v1/crm/kpis/scorecards` | computed live, off-track first |
| Review a KPI | `POST /api/v1/crm/kpis/reviews` | a minute of a meeting, so this one *does* keep the number |
| Read the plan tree | `POST /api/v1/crm/planning/tree` | the same gap subtraction, asked at every level |
| Sales performance | `POST /api/v1/crm/performance/sales` | attainment per person; null, not nought, for no number |
| Deal performance | `POST /api/v1/crm/performance/deals` | win rate, average size, and what has stalled |
| Executive board | `POST /api/v1/crm/board` | all five in one request, so two numbers cannot disagree |
| Declare a territory | `POST /api/v1/crm/territories` | rules, not a list — so a new account routes the moment it exists |
| Route something | `POST /api/v1/crm/territories/routes` | first match by priority, and it says how many it looked at |
| Territory coverage | `POST /api/v1/crm/territories/coverage` | the accounts in no territory — unaskable against a list |
| Assign a quota | `POST /api/v1/crm/quotas` | with a ramp, so a part-year seller is not reported as failing |
| Quota attainment | `POST /api/v1/crm/quotas/attainment` | assigned, committed and achieved side by side |
| Declare an approval process | `POST /api/v1/crm/approvals/processes` | criteria and steps an administrator changes without a deployment |
| Submit for approval | `POST /api/v1/crm/approvals/requests` | "no approval needed" is a real answer and is said out loud |
| Decide | `POST /api/v1/crm/approvals/decisions` | a submitter cannot approve their own request; a rejection ends it |
| Approval inbox | `POST /api/v1/crm/approvals/inbox` | filtered by who is asking, never by a user id in the body |
| Declare the week the desk is open | `POST /api/v1/crm/service/hours` | said back in minutes, because a typo in a week is invisible |
| Declare what the desk promises | `POST /api/v1/crm/service/policies` | one live promise per priority; a second retires the first |
| Raise a case | `POST /api/v1/crm/service/cases` | the promise is stamped once, in the hours the desk is open |
| Say something on a case | `POST /api/v1/crm/service/comments` | only a public reply from somebody else stops the response clock |
| The live queue | `POST /api/v1/crm/service/queue` | breach computed as of the read, not swept up by a job |
| Declare a campaign | `POST /api/v1/crm/campaigns` | a budget and a window; the spend is a separate ledger |
| Record a touch | `POST /api/v1/crm/campaigns/touches` | a replayed batch is told it is a replay, not counted twice |
| Record a spend | `POST /api/v1/crm/campaigns/costs` | append-only; over budget is reported, never refused |
| Campaign performance | `POST /api/v1/crm/campaigns/performance` | the model is named in the answer, and the gap is shown |
| Who influenced a deal | `POST /api/v1/crm/campaigns/attribution` | four models over one set of touches, with a cutoff at the decision |
| API description | `GET /openapi.json`, `GET /openapi` | generated from the manifest, so it cannot drift from the routes |

## The three tokens

There is no OIDC here. `Authentication.cs` maps three constants to claims and a real deployment
deletes it — everything downstream reads a `ClaimsPrincipal` and does not care who minted it.

| Token | Tenant | Scopes |
|---|---|---|
| `rep-northwind-token` | `crm-northwind` | `crm.read crm.write` |
| `manager-northwind-token` | `crm-northwind` | `crm.read crm.write crm.discount.approve crm.admin` |
| `rep-contoso-token` | `crm-contoso` | `crm.read crm.write` |

The tenant comes off the `tid` claim and off nothing else — not a header, not the payload
([ADR-0046](../../docs/adr/ADR-0046-a-tenant-is-resolved-at-admission.md)).

## The sequence

Capture a lead, and read the id back:

```bash
curl -sS -X POST http://localhost:5000/api/v1/crm/leads \
  -H 'Authorization: Bearer rep-northwind-token' \
  -H 'Idempotency-Key: lead-1' \
  -H 'Content-Type: application/json' \
  -d '{"company":"Northwind Traders","contactName":"Ada Rowe","email":"ada@northwind.test","source":0}'
```

Convert it into an account, a contact and an opportunity — one saga, three compensable steps:

```bash
curl -sS -X POST http://localhost:5000/api/v1/crm/lead-conversions \
  -H 'Authorization: Bearer rep-northwind-token' \
  -H 'Idempotency-Key: convert-1' \
  -H 'Content-Type: application/json' \
  -d '{"leadId":"<leadId>"}'
```

Quote it with a discount over the threshold — 300 off a subtotal of 1 000. The quote is written as a **Draft**;
the representative is not refused, because asking is theirs to do:

```bash
curl -sS -X POST http://localhost:5000/api/v1/crm/quotes \
  -H 'Authorization: Bearer rep-northwind-token' \
  -H 'Idempotency-Key: quote-1' \
  -H 'Content-Type: application/json' \
  -d '{"opportunityId":"<opportunityId>","discount":300,"validForDays":30,
       "lines":[{"sku":"SEAT","quantity":8,"unitPrice":{"amount":100,"currency":"EUR"}},
                {"sku":"SUPPORT","quantity":1,"unitPrice":{"amount":200,"currency":"EUR"}}]}'
```

Now the same approval, twice. The first is refused and the second is not:

```bash
# 403 — a representative does not hold crm.discount.approve
curl -sS -o /dev/null -w '%{http_code}\n' -X POST http://localhost:5000/api/v1/crm/quotes/approvals \
  -H 'Authorization: Bearer rep-northwind-token' \
  -H 'Content-Type: application/json' -d '{"quoteId":"<quoteId>"}'

# 200 — a manager does
curl -sS -X POST http://localhost:5000/api/v1/crm/quotes/approvals \
  -H 'Authorization: Bearer manager-northwind-token' \
  -H 'Content-Type: application/json' -d '{"quoteId":"<quoteId>"}'
```

And the order, which asks the threshold a second time:

```bash
curl -sS -X POST http://localhost:5000/api/v1/crm/orders \
  -H 'Authorization: Bearer rep-northwind-token' \
  -H 'Idempotency-Key: order-1' \
  -H 'Content-Type: application/json' -d '{"quoteId":"<quoteId>"}'
```

## The claim, in two `UPDATE`s

This is the part [§7](../../docs/26-CRM-Sample.md#7-dynamic-workflow--where-configuration-stops) exists for. Insert
a transition out of the opportunity's stage with a guard, and advance it:

```sql
INSERT INTO process_transition (transition_id, from_stage_id, to_stage_id, trigger, ordinal)
VALUES ('...', '<fromStage>', '<toStage>', 'advance', 1);

INSERT INTO transition_guard (guard_id, transition_id, field, operator, value)
VALUES ('...', '<transition>', 'amount', 'GreaterThan', '100000');

INSERT INTO transition_action (action_id, transition_id, kind, parameters, ordinal)
VALUES ('...', '<transition>', 'RequestApproval', '{"subject":"Approve the discount"}'::jsonb, 1);
```

```bash
curl -sS -X POST http://localhost:5000/api/v1/crm/opportunities/triggers \
  -H 'Authorization: Bearer rep-northwind-token' \
  -H 'Content-Type: application/json' \
  -d '{"opportunityId":"<opportunityId>","trigger":"advance"}'
```

A 50 000 opportunity does not move: the guard does not hold. Now lower the threshold — no
restart, no deployment, the same process still running:

```sql
UPDATE transition_guard SET value = '40000' WHERE transition_id = '<transition>';
```

Advance it again. It moves, and the approval task appears. That is the whole claim, and
`TransitionTests.AnAdministratorChangesBehaviourWithNoRebuild` is where it is asserted rather
than described.

**What it will not do is grow a sixth kind of action from the database.** `ActionKind` is a
closed enumeration compiled into one `switch`; a kind outside it is refused at publish time with
a message saying so. A new kind of side effect is a code change, a build and a deployment — that
is the price of compile-time orchestration, and it is paid in the open.

## Things to try that should fail

1. Ask for a quote whose lines are priced in two currencies — `crm.quote_mixes_currencies`, and
   nothing is written. Converting would need a rate, and a rate needs an instant.
2. Order a quote whose discount nobody approved — `crm.discount_not_approved`. The threshold is
   asked at the order too, so a quote issued before it moved cannot slip through.
3. Read another tenant's account with `rep-contoso-token` — `crm.account_not_found`, over the
   HTTP route and over the agent tool alike, with the same code.
4. Publish a guard naming a field outside the whitelist — refused at publish time, with the list
   of what a guard may name in the message.
5. Run the escalation sweep twice inside an hour — the second escalates nothing. The window is
   in the `WHERE` clause, not in how often the schedule fires.
6. Point `[AgentTrigger]` at `IssueQuoteFlow` and re-read `tests/Crm.Tests/AssistantTests`: what
   is published to a model is exactly what a model may do, and today that is one read.

---

**See also:** [26 — CRM Sample](../../docs/26-CRM-Sample.md) ·
[27 — CRM Reference Architecture](../../docs/27-CRM-Reference-Architecture.md) ·
[samples/event-driven](../event-driven/README.md) ·
[samples/polling](../polling/README.md) ·
[samples/ai-agent](../ai-agent/README.md)
