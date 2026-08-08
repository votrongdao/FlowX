/**
 * How wide the peek is, and why it is remembered.
 *
 * THREE SIZES, NOT TWO. A right-hand panel is the right shape for checking one value and the
 * wrong shape for reading a record with four sections in it — the fields wrap to one per line and
 * the reader scrolls a page that would have fitted on a screen. `wide` is the size that makes the
 * peek an answer rather than a preview, and `full` is for the case where the peek has stopped
 * being a peek and the reader wants the page.
 *
 * IT IS REMEMBERED PER BROWSER, because the choice is a working habit rather than a property of a
 * record: somebody who reads records wide reads every record wide, and having to say so on each
 * one is the kind of friction that ends with the peek being ignored in favour of the full page.
 * Same `localStorage` shape as the locale, and the same refusal to throw when storage is denied.
 */
export const DRAWER_SIZES = ['peek', 'wide', 'full'] as const

export type DrawerSize = (typeof DRAWER_SIZES)[number]

export const DEFAULT_DRAWER_SIZE: DrawerSize = 'peek'

const STORAGE_KEY = 'crm.drawerSize'

export function isDrawerSize(value: string | null): value is DrawerSize {
  return value !== null && (DRAWER_SIZES as readonly string[]).includes(value)
}

/** The size this browser last used, or the default. Never throws: private mode has no storage. */
export function storedDrawerSize(): DrawerSize {
  try {
    const saved = localStorage.getItem(STORAGE_KEY)

    return isDrawerSize(saved) ? saved : DEFAULT_DRAWER_SIZE
  } catch {
    return DEFAULT_DRAWER_SIZE
  }
}

export function rememberDrawerSize(size: DrawerSize): void {
  try {
    localStorage.setItem(STORAGE_KEY, size)
  } catch {
    // A browser that refuses storage still gets the size for this session.
  }
}

/**
 * The next size up, and the next size down, saturating at each end.
 *
 * Saturating rather than wrapping: a reader holding the widen key expects to arrive at the widest
 * and stop, and a wrap would take them from full screen back to a strip without them asking.
 */
export function widen(size: DrawerSize): DrawerSize {
  const next = DRAWER_SIZES[DRAWER_SIZES.indexOf(size) + 1]

  return next ?? size
}

export function narrow(size: DrawerSize): DrawerSize {
  const index = DRAWER_SIZES.indexOf(size)

  return index <= 0 ? size : (DRAWER_SIZES[index - 1] as DrawerSize)
}
