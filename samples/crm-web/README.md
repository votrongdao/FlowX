# Sample — the CRM's web client

**Claim proved:** the design handed over as an HTML prototype is implemented as a React
application whose data layer is the CRM's own API — twenty-two of its screens read and write the
real backend, and the rest render from the object model rather than from screen-specific mock-ups.

```bash
# The backend, on the port the dev server proxies to. CRM_SEED_FILE is what puts rows in
# the tenant; without it every screen renders an empty organisation. See samples/crm/README.md
# for why CRM_SEED_ALLOW_PRODUCTION is needed alongside it.
FLOWX_POSTGRES_CONNECTION="Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres" \
  CRM_SEED_FILE=samples/crm/seed/northwind.json CRM_SEED_ALLOW_PRODUCTION=true \
  dotnet run --project samples/crm

# The client.
cd samples/crm-web && npm install && npm run dev     # http://localhost:5173
```

**If the page never answers, name the interface.** Vite's default host is `localhost`, and it
binds to whatever that resolves to first — IPv4 on most machines, `::1` on some. On a host where
it picks IPv6, `npm run dev` prints `ready in 242 ms` while nothing at all is listening on
`127.0.0.1:5173`. `npm run dev -- --host 127.0.0.1` settles it; CI passes that flag for exactly
this reason.

`vite.config.ts` proxies `/api` to `http://localhost:5000` (override with `CRM_API`), so the
browser treats the API as same-origin. That is deliberate: the CORS policy in `Program.cs` is what
a deployed client needs, and a dev server that depended on it would hide a broken one.

## How it is put together

| Layer | Where | What it may know |
|---|---|---|
| Tokens | `src/design/tokens.css` | Colour, type, space, geometry. Nothing else names a colour. |
| Primitives | `src/design/primitives` | How things look and behave. **Nothing about a CRM.** |
| Charts | `src/design/charts` | Bars, waterfall, funnel, sparkline — layout, not a charting library. |
| Shell | `src/shell` | The chrome and the navigation table. |
| API | `src/api` | Routes, contracts, one hook per backend surface. |
| Features | `src/features/*` | Composition. A screen is primitives plus one hook. |

Three rules hold the layering up, and each is worth stating because breaking it is cheap and the
cost arrives later:

- **A primitive never imports a feature, a contract or a query.** That is what lets the button in
  the executive board be the same button as the one in setup.
- **A screen never calls `fetch`.** Every read and write goes through `src/api/queries/hooks.ts`,
  which is the only file that knows a route, a token, an idempotency rule and a cache key. The
  fourth screen is where writing that four times starts costing.
- **Every cache key lives in `src/api/queries/keys.ts`.** A key spelled in two files is two caches,
  and the symptom is a mutation that refreshes the list but not the tile above it.

## What is wired to the backend

| Screen | Endpoint |
|---|---|
| Service console, case board | `POST /service/queue`, `/service/comments` |
| Business hours and SLA | `POST /service/hours`, `/service/policies` |
| Work inbox | `POST /approvals/inbox`, `/approvals/decisions`, `/service/queue` |
| Approvals setup | `POST /approvals/processes` |
| Quote builder — submit | `POST /approvals/requests` |
| Campaign performance, attribution | `POST /campaigns/performance`, `/campaigns/attribution` |
| Executive board, exec home | `POST /board` |
| Scorecard, reviews | `POST /kpis/scorecards` |
| Sales and deal performance | `POST /performance/sales`, `/performance/deals`, `/quotas/attainment` |
| Portfolio, strategy | `POST /planning/roll-ups`, `/planning/tree` |
| Search | `POST /search` |

The remaining screens — the record surfaces, the setup editors, the planning detail — read
`src/fixtures/objects.ts`, which carries the prototype's own object model and records. **The
backend has no list or record endpoint for the built-in entities**, so those screens read the
model rather than inventing an API the server does not serve. Each hook they would use has the
same shape as a query hook, so wiring one up later is a change to one import.

## What is worth reading

- `src/design/primitives/DataTable.tsx` — sorting is opt-in per column, because a header offering
  to sort a column the server ordered would silently reorder a page rather than a result.
- `src/api/client.ts` — every refusal is a problem document with a code and a sentence written for
  a person. Throwing that away and rendering "something went wrong" discards the only useful part.
- `src/features/analytics/CampaignScreen.tsx` — the attribution model is named beside every number
  it produced, and the gap between what was considered and what was attributed is shown rather
  than closed.
- `src/features/sales/KanbanScreen.tsx` — cards move by drag *and* by arrow key, and the move says
  it is not written: the backend advances an opportunity by announcing an intent, not by being
  told which column to put it in.

## Checks

```bash
npm run typecheck    # strict, with noUncheckedIndexedAccess and exactOptionalPropertyTypes
npm test             # the pure pieces, no processes behind them
npm run build
```

And the browser suite, which needs a client, an API and a seeded database to point at:

```bash
CRM_E2E_BASE_URL=http://127.0.0.1:5173 npm run test:e2e

# On a machine that already carries a Chromium — a container image, usually — point at it
# rather than letting Playwright fetch one whose revision it happens to prefer.
CRM_E2E_BASE_URL=http://127.0.0.1:5173 CRM_E2E_CHROMIUM=/opt/pw-browsers/chromium npm run test:e2e
```

**Seeded** is load-bearing in that sentence. Against an empty tenant this suite does not fail
fast: forty of its sixty-five tests wait ninety seconds each for a deal, a quote or a process
that was never written, and the ones that pass are the ones about an empty organisation.

It walks one path per role — the seller from an opportunity to a refused order, the manager
through the inbox and the discount, the director through the reporting line and a KPI review —
and asserts on the sentences the server sent back. Unset the variable and it skips; point it at
somewhere nothing is listening and it fails, which is the distinction that matters.

The tests cover the two places a defect would be invisible: the formatters, where a null that
renders as `0%` puts a campaign that reached nobody below one that converted one in a thousand;
and the primitives' behaviour, where a card that navigates without being a button, a tab strip
that only answers a mouse, and a hint that is rendered but not announced all look correct on
screen. Each was proved by breaking it and watching the right test fail.
