import { useMemo, useState } from 'react'
import { useEntityPage } from '@/api/queries/hooks'
import { useSession } from '@/session/SessionProvider'
import type { RecordView } from '@/api/contracts'

/**
 * Everything the sales console needs, from the tenant's own rows.
 *
 * THE LANDING PAGE MADE NO REQUEST AT ALL. Pipeline, weighted value, won this quarter, what is
 * closing and the task list were all derived from the prototype's fixture records, filtered by
 * `owner === 'A. Ruiz'` — a name in a file, not the person signed in. The first screen anybody
 * opens showed a pipeline belonging to nobody.
 *
 * WHY A HOOK AND NOT SIX `useMemo`s IN THE COMPONENT. The console's numbers are all views of the
 * same opportunity set, and the one thing they must do is agree: the pipeline total in the tile,
 * the sum of the funnel and the closing table are the same money read three ways. Deriving them
 * together, from one filtered list, is what makes that true by construction rather than by
 * everybody remembering to apply the same filter.
 *
 * THE STAGES ARE THE PUBLISHED PROCESS'S, WHICH IS WHY THE FUNNEL IS BUILT FROM THE ROWS. A stage
 * list this file held would be the stages it was compiled with; grouping by what the rows actually
 * carry means a stage an administrator added appears the moment a deal enters it.
 */

/**
 * A `Date`'s calendar day where the person is, as `YYYY-MM-DD`.
 *
 * `toISOString().slice(0, 10)` is the obvious way to write this and it is wrong everywhere east
 * of Greenwich: `new Date(2026, 9, 1)` is local midnight on 1 October, which in UTC+07 is
 * 2026-09-30T17:00Z, so the string comes back a day early. The boundaries here are compared
 * against `close_date`, which the server sends as a plain calendar date with no zone at all, so
 * the comparison has to be made in the same terms.
 *
 * The visible symptom was the whole point of this function: with the quarter ending a day early,
 * an opportunity closing on the last day of the quarter fell outside "this quarter" — the default
 * filter on the first screen of the application — and every reader in Asia, Australia or eastern
 * Europe was shown an empty pipeline where a reader in London was shown a full one.
 */
function localDay(date: Date): string {
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const day = String(date.getDate()).padStart(2, '0')

  return `${date.getFullYear()}-${month}-${day}`
}

export interface HorizonBounds {
  /** The local calendar day, as the server writes `close_date`. */
  today: string
  /** Exclusive: the first day of next month. */
  monthEnd: string
  /** Exclusive: the first day of the next quarter. */
  quarterEnd: string
}

/**
 * The two boundaries the horizon filter compares against, plus today.
 *
 * Exported and taking `now` as an argument so the boundaries can be tested at a timezone and a
 * date, which is the only way to pin behaviour that was correct in one hemisphere of offsets and
 * wrong in the other. `useConsole` passes the real clock.
 */
export function horizonBounds(now: Date): HorizonBounds {
  return {
    today: localDay(now),
    monthEnd: localDay(new Date(now.getFullYear(), now.getMonth() + 1, 1)),
    quarterEnd: localDay(new Date(now.getFullYear(), (Math.floor(now.getMonth() / 3) + 1) * 3, 1)),
  }
}

export type OwnerFilter = 'mine' | 'all'
export type HorizonFilter = 'quarter' | 'month' | 'open'
export type OutcomeFilter = 'all' | 'open' | 'Won' | 'Lost'

export interface ConsoleFilters {
  owner: OwnerFilter
  horizon: HorizonFilter
  outcome: OutcomeFilter
}

const DEFAULTS: ConsoleFilters = { owner: 'mine', horizon: 'quarter', outcome: 'open' }

export interface StageTotal {
  stage: string
  count: number
  sum: number
}

export interface ConsoleModel {
  filters: ConsoleFilters
  setFilter: <K extends keyof ConsoleFilters>(key: K, value: ConsoleFilters[K]) => void
  clear: () => void
  /** How many of the filters are not at their default, for the "showing…" line. */
  activeCount: number
  isPending: boolean
  error: Error | null
  open: readonly Deal[]
  won: readonly Deal[]
  totals: readonly StageTotal[]
  openValue: number
  weightedValue: number
  wonValue: number
  closingThisMonth: readonly Deal[]
  tasks: readonly Task[]
  overdueTasks: number
}

/** One opportunity, with the strings a page returns turned into the numbers a total needs. */
export interface Deal {
  id: string
  name: string
  account: string | null
  stage: string
  amount: number
  probability: number
  closeDate: string | null
  outcome: string | null
  ownerId: string | null
}

/** One activity. `overdue` is decided here against one clock, which is the browser's. */
export interface Task {
  id: string
  subject: string
  kind: string
  status: string
  dueAt: string | null
}

export function useConsole(): ConsoleModel {
  const { ownerId } = useSession()

  const deals = useEntityPage('Opportunity')
  const activities = useEntityPage('Activity')

  const [filters, setFilters] = useState<ConsoleFilters>(DEFAULTS)

  const all = useMemo(() => (deals.data?.records ?? []).map(toDeal), [deals.data])

  const model = useMemo(() => {
    // Boundaries from today rather than from a date this file was written on. The fixtures had a
    // hard-coded "today" so their dates read as this quarter; live rows are dated whenever the
    // tenant made them, and a fixed reference would call every one of them historic.
    const { today, monthEnd, quarterEnd } = horizonBounds(new Date())

    const inScope = all.filter((deal) => {
      // "Mine" is answerable and "my team's" is not: an opportunity carries an owner uuid and the
      // reporting line is keyed by the subject a token carries. They are different identity
      // spaces, so a team filter here would be a guess. The executive board is scoped by the line
      // on the server, which is where that question belongs.
      if (filters.owner === 'mine' && deal.ownerId !== ownerId) return false

      if (filters.outcome === 'open' && deal.outcome !== null) return false
      if (filters.outcome === 'Won' && deal.outcome !== 'Won') return false
      if (filters.outcome === 'Lost' && deal.outcome !== 'Lost') return false

      const close = deal.closeDate ?? ''

      if (filters.horizon === 'quarter' && close >= quarterEnd) return false
      if (filters.horizon === 'month' && close >= monthEnd) return false

      return true
    })

    const open = inScope.filter((deal) => deal.outcome === null)
    const won = inScope.filter((deal) => deal.outcome === 'Won')

    // Grouped by the stage each row is actually in, in the order they first appear — which is the
    // order the page returned them in, which is the process's ordinal.
    const byStage = new Map<string, StageTotal>()

    for (const deal of open) {
      const total = byStage.get(deal.stage) ?? { stage: deal.stage, count: 0, sum: 0 }

      total.count += 1
      total.sum += deal.amount
      byStage.set(deal.stage, total)
    }

    return {
      open,
      won,
      totals: [...byStage.values()],
      closingThisMonth: open.filter(
        (deal) => (deal.closeDate ?? '') >= today && (deal.closeDate ?? '') < monthEnd,
      ),
    }
  }, [all, filters, ownerId])

  const tasks = useMemo(
    () => (activities.data?.records ?? []).map(toTask).filter((task) => task.status !== 'Completed'),
    [activities.data],
  )

  const openValue = model.open.reduce((sum, deal) => sum + deal.amount, 0)

  const weightedValue = Math.round(
    model.open.reduce((sum, deal) => sum + (deal.amount * deal.probability) / 100, 0),
  )

  const activeCount =
    (filters.owner === DEFAULTS.owner ? 0 : 1) +
    (filters.horizon === DEFAULTS.horizon ? 0 : 1) +
    (filters.outcome === DEFAULTS.outcome ? 0 : 1)

  const today = new Date().toISOString()

  return {
    filters,
    setFilter: (key, value) => setFilters((current) => ({ ...current, [key]: value })),
    clear: () => setFilters(DEFAULTS),
    activeCount,
    isPending: deals.isPending || activities.isPending,
    error: deals.error ?? activities.error,
    open: model.open,
    won: model.won,
    totals: model.totals,
    openValue,
    weightedValue,
    wonValue: model.won.reduce((sum, deal) => sum + deal.amount, 0),
    closingThisMonth: model.closingThisMonth,
    tasks,
    overdueTasks: tasks.filter((task) => task.dueAt !== null && task.dueAt < today).length,
  }
}

/**
 * One page row as a deal.
 *
 * EVERY VALUE IN A PAGE IS TEXT, including the numbers: the projection returns strings so a filter
 * can compare them. Summing them as strings concatenates, which is the one arithmetic mistake that
 * produces a plausible-looking total.
 */
function toDeal(record: RecordView): Deal {
  return {
    id: record.recordId,
    name: record.values['name'] ?? '—',
    account: record.values['account_id'] ?? null,
    stage: record.values['stage'] ?? '—',
    amount: Number(record.values['amount'] ?? 0),
    probability: Number(record.values['probability'] ?? 0),
    closeDate: record.values['expected_close'] ?? null,
    outcome: record.values['outcome'] ?? null,
    ownerId: record.values['owner_id'] ?? null,
  }
}

function toTask(record: RecordView): Task {
  return {
    id: record.recordId,
    subject: record.values['subject'] ?? '—',
    kind: record.values['kind'] ?? 'Task',
    status: record.values['status'] ?? 'Open',
    dueAt: record.values['due_at'] ?? null,
  }
}
