# ADR-0076: A directly editable field is a closed enumeration, not a generic patch

**Status:** Proposed
**Date:** 2026-08-08
**Deciders:** Platform architecture, CRM sample

## Context

A record page shows a value and offers no way to change it. `EditFieldsDrawer` writes
**custom** fields through `crm.custom.entity`, and says so in its own header: *"NOTHING
HERE EDITS A BUILT-IN COLUMN. There is no write for those on this build, and a form that
offered one would be a form whose Save did nothing."* Nothing edits `account.name`,
`contact.email` or `opportunity.close_date`, and no flow in the sample's eighty-three
does.

That is not an oversight. Every write to a built-in entity is an *intent* —
`crm.lead.capture`, `crm.lead.convert`, `crm.opportunity.advance`, `crm.quote.issue`,
`crm.order.place` — which is [ADR-0001](ADR-0001-flow-and-capability-as-primitives.md)
holding: a capability is one unit of business meaning, and "set column to value" is not
one.

But some values are genuinely just values. A misspelt company name, a phone number that
gained a digit, an industry picked wrongly at capture. Sending those through an intent
flow named after them produces flows named `crm.account.rename` whose business meaning is
"the name was wrong", which is not a meaning — it is an admission that the first write had
a typo in it. A platform whose answer to a typo is a new capability is a platform whose
capability count tracks its users' spelling.

So the question is not *whether* a field can be edited directly. It is **who decides which
ones**, and **when that decision is checked**.

Options considered:

- **A. A generic `crm.record.patch` flow.** One flow, `(entity, id, {field: value})`,
  authorised by `field_policy`. Cheapest to build and the shape every CRUD framework
  offers. *Rejected:* it makes every write expressible as a patch, so the intent flows
  become optional decoration — the same effect reachable two ways, one of which carries no
  meaning. The manifest then describes an application whose capabilities are a fiction:
  `crm.opportunity.advance` says a stage change runs the process, while
  `crm.record.patch` can set `stage` and run nothing. That is not a style objection. It is
  the guarantee in [13-AI-Native](../13-AI-Native.md) — that the manifest is what the
  application actually does — failing for every entity with a guarded column.

- **B. One intent flow per editable field.** `crm.account.rename`,
  `crm.contact.recontact`, and so on. Faithful, and unbounded: seven entities against
  roughly ten columns each is seventy flows whose Define bodies are one step apiece.
  *Rejected:* the count is the argument. Seventy capabilities that each set one column
  make the capability catalogue useless for the thing it is for — reading what an
  application can do.

- **C. One flow, `crm.record.field.set`, over a closed enumeration of `(entity, column)`
  pairs the compiler sees the whole of.** Configuration selects *which* pair and *what*
  value; it cannot add a pair. **Chosen.**

- **D. Defer until the manifest v1.0 freeze
  ([ADR-0017](ADR-0017-manifest-v1-freeze-criteria.md)).** *Rejected:* the freeze is about
  manifest fields, and this adds none — `crm.record.field.set` is one more flow with one
  more contract. Deferring costs the sample its most-asked-for interaction for no
  criterion's benefit.

## Decision

We will add **one** flow, `crm.record.field.set`, whose editable surface is a **closed
enumeration** declared in code and validated at compile time.

This is not a new pattern in this repository. It is the one already drawn for configured
processes in [26 §7.3](../26-CRM-Sample.md): an administrator rewrites transitions, guards
and actions at run time, while *the set of action kinds stays closed and the compiler sees
the whole of it* — `ProcessPublishing.Validate` is where a configuration naming an unknown
action kind is refused. The same division applies here. What a tenant may edit is
configuration; **what is editable at all is not**.

Concretely:

1. `EditableColumn` enumerates the permitted `(EntityKind, column)` pairs. A column is in
   it when changing it means *the previous value was wrong*, and out of it when changing
   it means *something happened* — the second is an intent and keeps its flow.
   `opportunity.stage` stays out ([ADR-0031](ADR-0031-an-occurrence-names-the-instance-it-starts.md)
   has the process running behind it); `opportunity.amount` stays out once a quote has been
   issued against it; `account.name` and `contact.email` are in.

2. An analyzer emits **FLOWX1035** when a member is added to `EditableColumn` naming a
   column that `Describe` does not answer for, and an architecture fitness function fails
   the build when a pair in the enumeration is also reachable through an intent flow —
   the two-ways-to-write defect that killed option A, refused mechanically rather than by
   review.

3. The capability consults `field_policy` for `canWrite` before anything else, evaluates
   the tenant's `custom_validation_rule` set through the existing `GuardOperator`
   vocabulary, and refuses with a `Result.Fail` naming the rule. No new validation
   language.

4. The write journals to `custom_field_history`, whose `(tenant, entity, record, field,
   old, new, at, by)` shape already fits a built-in column and today only ever holds a
   custom one.

5. It emits `record.field.changed`, which is what a follower notification, a change-feed
   subscriber and an audit read can all hang off without any of them knowing about the
   others.

The flow is `Ephemeral`. One row changes, there is nothing to unwind, and a journal row
per typo correction is [ADR-0003](ADR-0003-execution-profiles.md)'s definition of paying
for durability nobody asked for.

## Consequences

**Positive**
- The record page can offer inline editing without the platform acquiring a generic write.
- The manifest stays honest: a reader sees exactly which columns are directly writable,
  because the enumeration is in it.
- Field-level authorisation and tenant validation reach a surface they were written for and
  never covered — `field_policy` exists today and nothing in the sample writes through it.
- `custom_field_history` stops being custom-only, which is most of an audit trail for free.

**Negative / accepted trade-offs**
- **Adding an editable column is a code change and a release.** Accepted, and it is the
  point: the alternative is a tenant making `stage` editable and silently routing around
  the configured process.
- **The enumeration will be argued over**, column by column, and some arguments will be
  close. Accepted — the argument is the design work, and having it once per column beats
  discovering the answer from a support ticket.
- **Two ways to change `account.name` exist in principle** — this flow, and a future
  intent that happens to touch it. The fitness function in (2) is what keeps that from
  becoming true in fact; without it this ADR decays into option A over about a year.
- **`custom_field_history` grows faster**, and it has no retention policy. Not addressed
  here; it is a table-lifecycle question that the change feed and the journal both share.

**Revisit when:** the enumeration passes roughly twenty-five pairs — at which point "the
compiler sees the whole of it" has stopped being a thing a person can hold in their head
and the closed set is doing less work than it costs — or when a tenant-defined editable
set becomes a real requirement rather than an anticipated one, which is the point where
this ADR and [26 §7.3](../26-CRM-Sample.md) have to be reopened together, because they are
the same decision made twice.
