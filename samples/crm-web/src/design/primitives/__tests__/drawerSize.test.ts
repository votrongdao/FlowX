/**
 * The peek's size, as a state machine and as a stored preference.
 *
 * The interesting cases are the two ends and the browser that refuses storage. A wrap at either
 * end would take a reader from full screen back to a strip without them asking, and a throw from
 * `localStorage` would take the whole record page with it — Safari in private mode throws on
 * `setItem`, and a peek is not worth a blank screen.
 */
import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  DEFAULT_DRAWER_SIZE,
  isDrawerSize,
  narrow,
  rememberDrawerSize,
  storedDrawerSize,
  widen,
} from '../drawerSize'

afterEach(() => {
  localStorage.clear()
  vi.restoreAllMocks()
})

describe('widen and narrow', () => {
  it('steps up through the three sizes', () => {
    expect(widen('peek')).toBe('wide')
    expect(widen('wide')).toBe('full')
  })

  it('steps back down', () => {
    expect(narrow('full')).toBe('wide')
    expect(narrow('wide')).toBe('peek')
  })

  it('saturates rather than wrapping', () => {
    expect(widen('full')).toBe('full')
    expect(narrow('peek')).toBe('peek')
  })
})

describe('the stored preference', () => {
  it('round-trips', () => {
    rememberDrawerSize('wide')

    expect(storedDrawerSize()).toBe('wide')
  })

  it('falls back to the default when nothing is stored', () => {
    expect(storedDrawerSize()).toBe(DEFAULT_DRAWER_SIZE)
  })

  it('falls back when the stored value is not a size', () => {
    // A key this application wrote in an earlier shape, or one somebody edited by hand.
    localStorage.setItem('crm.drawerSize', 'enormous')

    expect(storedDrawerSize()).toBe(DEFAULT_DRAWER_SIZE)
  })

  it('does not throw when storage is denied on read', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('The operation is insecure.')
    })

    expect(storedDrawerSize()).toBe(DEFAULT_DRAWER_SIZE)
  })

  it('does not throw when storage is denied on write', () => {
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('The quota has been exceeded.')
    })

    expect(() => rememberDrawerSize('full')).not.toThrow()
  })
})

describe('isDrawerSize', () => {
  it('accepts the three and refuses everything else', () => {
    expect(isDrawerSize('peek')).toBe(true)
    expect(isDrawerSize('full')).toBe(true)
    expect(isDrawerSize('wider')).toBe(false)
    expect(isDrawerSize(null)).toBe(false)
  })
})
