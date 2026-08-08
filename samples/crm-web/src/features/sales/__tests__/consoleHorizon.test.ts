/**
 * The console's horizon boundaries, pinned against the timezone that broke them.
 *
 * These run under `TZ=Asia/Bangkok` (UTC+07) rather than the machine's own zone, because the bug
 * they cover is invisible at UTC and at every negative offset: `new Date(2026, 9, 1)` is local
 * midnight, `toISOString()` moves it back seven hours, and the quarter ends on 30 September
 * instead of 1 October. A test that ran in the developer's zone would pass in London and fail in
 * Hanoi, which is exactly the failure being fixed.
 */
import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import { horizonBounds } from '../useConsole'

const REAL_TZ = process.env.TZ

describe('horizonBounds, east of UTC', () => {
  beforeAll(() => {
    process.env.TZ = 'Asia/Bangkok'
  })

  afterAll(() => {
    process.env.TZ = REAL_TZ
  })

  it('ends the quarter on the first day of the next one, not the day before', () => {
    // 8 August is in Q3, which runs to 30 September inclusive — so the exclusive bound is 1 Oct.
    expect(horizonBounds(new Date(2026, 7, 8, 12)).quarterEnd).toBe('2026-10-01')
  })

  it('keeps a deal closing on the last day of the quarter inside it', () => {
    const { quarterEnd } = horizonBounds(new Date(2026, 7, 8, 12))

    // This is the comparison the filter makes. Before the fix it was true, and the first screen
    // of the application showed an empty pipeline to every reader in this timezone.
    expect('2026-09-30' >= quarterEnd).toBe(false)
  })

  it('ends the month on the first of the next month', () => {
    expect(horizonBounds(new Date(2026, 7, 8, 12)).monthEnd).toBe('2026-09-01')
  })

  it('reads today as the local calendar day, just after local midnight', () => {
    // 00:30 local on 9 August is 17:30Z on 8 August. `toISOString()` calls that yesterday.
    expect(horizonBounds(new Date(2026, 7, 9, 0, 30)).today).toBe('2026-08-09')
  })

  it('ends December in the next year rather than wrapping to January of this one', () => {
    const { quarterEnd, monthEnd } = horizonBounds(new Date(2026, 11, 15, 12))

    expect(quarterEnd).toBe('2027-01-01')
    expect(monthEnd).toBe('2027-01-01')
  })
})
