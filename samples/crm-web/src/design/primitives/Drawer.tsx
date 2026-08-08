import { useEffect, useRef } from 'react'
import { createPortal } from 'react-dom'
import type { ReactNode } from 'react'
import { Button } from './Button'
import { narrow, widen } from './drawerSize'
import type { DrawerSize } from './drawerSize'
import styles from './Drawer.module.css'

export interface DrawerProps {
  /** The dialog's name, announced when it opens. */
  title: ReactNode
  /** The small line above the title — a record number, a code. */
  eyebrow?: ReactNode
  /** The line under the title — an account, a tier. */
  subtitle?: ReactNode
  /** Buttons under the heading. */
  actions?: ReactNode
  onClose: () => void
  width?: number
  /**
   * How much of the screen it takes. Omit it and the drawer is the strip it always was — the
   * forms that open one are the size of their fields and have nothing to widen into.
   */
  size?: DrawerSize
  /** Supplied with {@link size} to offer the widen and narrow controls. */
  onSizeChange?: (size: DrawerSize) => void
  children: ReactNode
}

/**
 * The right-hand peek panel.
 *
 * **Escape closes it and focus goes inside when it opens.** A peek that traps neither is one a
 * keyboard user opens and then cannot leave; both are a handful of lines and both are the first
 * things reported when this pattern ships without them.
 */
export function Drawer({
  title,
  eyebrow,
  subtitle,
  actions,
  onClose,
  width,
  size,
  onSizeChange,
  children,
}: DrawerProps) {
  const panel = useRef<HTMLDivElement>(null)
  const opener = useRef<Element | null>(null)

  useEffect(() => {
    opener.current = document.activeElement
    panel.current?.focus()

    function onKeyDown(event: KeyboardEvent) {
      if (event.key !== 'Escape') return

      // Escape steps back down through the sizes before it closes. A reader who went full screen
      // and pressed Escape meant "give me the page back", not "throw away what I was reading" —
      // and the second is not undoable, because the peek does not remember which record it held.
      if (size && onSizeChange && size !== 'peek') {
        onSizeChange(narrow(size))
        return
      }

      onClose()
    }

    document.addEventListener('keydown', onKeyDown)

    return () => {
      document.removeEventListener('keydown', onKeyDown)
      // Focus goes back where it came from. Without this it lands on <body> and the next Tab
      // starts at the top of the page, which is the whole document away from where they were.
      if (opener.current instanceof HTMLElement) opener.current.focus()
    }
  }, [onClose])

  /*
   * PORTALLED TO THE DOCUMENT, so a drawer opened from inside a drawer covers the window rather
   * than the panel it was opened from. That became reachable when the list's peek started drawing
   * `RecordDetail`, which has an Edit of its own: rendered in place, the edit panel was laid out
   * inside the peek and clipped by it, which reads as a broken control rather than a nested one.
   * `.shell` is the only positioned ancestor and it fills the window, so `fixed` covers the same
   * rectangle `absolute` did — this moves no pixels for the nine drawers that were already fine.
   */
  return createPortal(
    <div
      className={styles.scrim}
      onClick={(event) => {
        if (event.target === event.currentTarget) onClose()
      }}
    >
      <div
        ref={panel}
        role="dialog"
        aria-modal="true"
        aria-label={typeof title === 'string' ? title : undefined}
        tabIndex={-1}
        className={`${styles.panel} ${size ? (styles[size] ?? '') : ''}`}
        style={
          width && (size ?? 'peek') === 'peek'
            ? ({ '--drawer-width': `${width}px` } as React.CSSProperties)
            : undefined
        }
      >
        <header className={styles.head}>
          <div className={styles.headTop}>
            {eyebrow ? <span className={styles.eyebrow}>{eyebrow}</span> : null}
            <div className={styles.headControls}>
              {size && onSizeChange ? (
                <>
                  {/*
                    Two buttons rather than one toggle. A single "expand" that cycles peek → wide →
                    full → peek is one click away from the size you wanted and three from the one
                    you left, and a reader cannot tell which way it will go before pressing it.
                  */}
                  <Button
                    iconOnly
                    size="sm"
                    aria-label="Narrow"
                    disabled={size === 'peek'}
                    onClick={() => onSizeChange(narrow(size))}
                  >
                    ›
                  </Button>
                  <Button
                    iconOnly
                    size="sm"
                    aria-label="Widen"
                    disabled={size === 'full'}
                    onClick={() => onSizeChange(widen(size))}
                  >
                    ‹
                  </Button>
                </>
              ) : null}
              <Button
                iconOnly
                size="sm"
                aria-label="Close"
                className={styles.close}
                onClick={onClose}
              >
                ✕
              </Button>
            </div>
          </div>
          <div className={styles.title}>{title}</div>
          {subtitle ? <div className={styles.subtitle}>{subtitle}</div> : null}
          {actions ? <div className={styles.actions}>{actions}</div> : null}
        </header>
        <div className={styles.body}>{children}</div>
      </div>
    </div>,
    document.body,
  )
}

export function DrawerSection({ label, note }: { label: string; note?: string }) {
  return (
    <div className={styles.sectionLabel}>
      <span>{label}</span>
      {note ? <span className={styles.sectionNote}>{note}</span> : null}
    </div>
  )
}

export interface Highlight {
  label: string
  value: ReactNode
  tone?: 'default' | 'positive' | 'warning' | 'critical'
}

const HIGHLIGHT_COLOUR: Record<string, string> = {
  default: 'var(--color-text)',
  positive: 'var(--color-positive)',
  warning: 'var(--color-warning)',
  critical: 'var(--color-critical)',
}

/** The strip of numbers under a peek panel's heading. */
export function DrawerHighlights({ items }: { items: readonly Highlight[] }) {
  return (
    <div className={styles.highlights}>
      {items.map((item) => (
        <div key={item.label} style={{ minWidth: 0 }}>
          <div className={styles.highlightLabel}>{item.label}</div>
          <div
            className={styles.highlightValue}
            style={{ color: HIGHLIGHT_COLOUR[item.tone ?? 'default'] }}
          >
            {item.value}
          </div>
        </div>
      ))}
    </div>
  )
}
