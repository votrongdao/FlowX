import { useNavigate } from '@tanstack/react-router'
import { useMemo, useState } from 'react'
import {
  Button,
  ButtonGroup,
  DataTable,
  ErrorState,
  Drawer,
  Page,
  PageHeader,
  TextField,
} from '@/design/primitives'
import type { Column } from '@/design/primitives'
import { useEntityPage, useProcess, useSchema } from '@/api/queries/hooks'
import { modelFor } from '@/fixtures/objects'
import type { RecordRow } from '@/fixtures/objects'
import { isNumeric, renderCell } from './RecordCell'
import { RecordDetail } from './RecordScreen'
import { entityOf, toRows } from './liveRecords'
import { rememberDrawerSize, storedDrawerSize } from '@/design/primitives/drawerSize'
import type { DrawerSize } from '@/design/primitives/drawerSize'
import { NewLeadDrawer } from './NewLeadDrawer'
import { NewTaskDrawer } from './NewTaskDrawer'
import styles from './ListScreen.module.css'


/**
 * Why the other objects have no New button.
 *
 * <strong>"Not wired yet" was three wrong things at once.</strong> It read as an unfinished
 * client, it produced "a account" and "a opportunity", and it was untrue: an account is not
 * missing a form, it is a record this system creates by converting a lead. Saying which act
 * produces the record tells a reader what to do instead; saying "not wired" tells them to wait.
 */
const WHY_NOT: Readonly<Record<string, string>> = {
  account: 'An account is created by converting a lead, not from a form.',
  contact: 'A contact is created by converting a lead, not from a form.',
  opportunity: 'An opportunity is created by converting a lead, not from a form.',
  quote: 'A quote is priced against an opportunity — open one and issue it there.',
  workorder: 'An order is placed against an issued quote, not created directly.',
}

/** The objects this build has a write for. Everything else says so rather than pretending. */
const CAN_CREATE: readonly string[] = ['lead', 'task']

/**
 * The list view — one screen for every object, driven by the object's own model.
 *
 * SEVEN OBJECTS AND ONE COMPONENT. The prototype renders each list from the same metadata, and so
 * does this: the columns, the filters and the peek panel are read from `listCols`, `fields` and
 * `layout`. A new object is a row in the model, not a new screen — which is the whole claim the
 * backend's dynamic schema makes, honoured on the client rather than contradicted by it.
 */
export function ListScreen({ objectKey }: { objectKey: string }) {
  const navigate = useNavigate()
  const model = modelFor(objectKey)

  const [search, setSearch] = useState('')
  const [stage, setStage] = useState<string>('all')
  const [peek, setPeek] = useState<RecordRow | null>(null)

  // The size outlives the peek: closing one record and opening the next keeps the width the
  // reader chose, which is the whole reason it is remembered rather than reset per record.
  const [size, setSizeState] = useState<DrawerSize>(storedDrawerSize)

  const setSize = (next: DrawerSize) => {
    setSizeState(next)
    rememberDrawerSize(next)
  }
  const [hidden, setHidden] = useState<readonly string[]>([])
  const [showColumns, setShowColumns] = useState(false)
  const [creating, setCreating] = useState(false)

  // Every one of the seven objects is a table this build has, and the server pages all of them.
  const entity = entityOf(objectKey)
  const page = useEntityPage(entity)

  // The board is the published process, and only opportunities have one. The button used to
  // appear on every object with a stage field — leads, quotes, orders and tasks — and every one
  // of them navigated to the opportunity board. Four buttons, four wrong destinations.
  const process = useProcess(entity === 'Opportunity' ? 'Opportunity' : null)

  // What a built-in column may hold, from the tenant's own description. One cached call, shared
  // with every setup screen.
  const schema = useSchema()

  // Contacts and opportunities carry an account id. The accounts page answers what it is called,
  // and it is one cached request rather than one per row.
  const accounts = useEntityPage(entity === 'Contact' || entity === 'Opportunity' ? 'Account' : null)

  const accountNames = useMemo(() => {
    const names = new Map<string, string>()

    for (const record of accounts.data?.records ?? []) {
      const name = record.values['name']

      if (name !== null && name !== undefined) {
        names.set(record.recordId, name)
      }
    }

    return names
  }, [accounts.data])

  // THE FIXTURES USED TO BE THE FALLBACK, AND A GREY LABEL WAS THE ONLY WARNING. While the page
  // loaded — and, worse, whenever it failed — this list rendered the prototype's invented rows:
  // Northwind Systems, a €184,000 deal, five contacts, all clickable, all opening a record id
  // that resolves to nothing. "sample data — the server did not answer" in the eyebrow does not
  // make a table of somebody else's records honest, and nobody reads an eyebrow.
  const source = useMemo(
    () => toRows(objectKey, model, page.data?.records ?? [], accountNames),
    [objectKey, model, page.data, accountNames],
  )

  /**
   * The vocabulary the server declares for the filtered column, or nothing.
   *
   * MATCHED BY COLUMN NAME, WHICH IS NOT ALWAYS THIS SCREEN'S NAME. `liveRecords` renames some
   * columns on the way in — an account's `lifecycle` is `type` here — so a stage field that is
   * not also the server's own column name finds nothing and falls back below. Guessing the
   * inverse of that table would be a second copy of it, and a wrong guess draws a picker of
   * values from a different column, which reads as data rather than as a bug.
   *
   * Empty is also the answer for quotes, orders and tasks: `describe` covers the four kinds a
   * tenant can extend, and a status of the other three is still row-derived.
   */
  const declared = useMemo(() => {
    if (model.stageField === undefined || entity === null) {
      return []
    }

    return (
      schema.data?.entities
        .find((described) => described.kind === entity)
        ?.columns.find((column) => column.name === model.stageField)?.options ?? []
    )
  }, [entity, model.stageField, schema.data])

  /**
   * The values this filter offers.
   *
   * IT WAS THE PROTOTYPE'S PICKLIST. `optionsFor` read a list of stage names out of this client,
   * so the opportunity strip offered Prospecting…Closed Lost whatever the tenant's published
   * process actually said — a chip for a stage nobody had declared filters to nothing, and a
   * stage an administrator added had no chip at all.
   *
   * THREE SOURCES, IN THE ORDER OF WHAT KNOWS. For an opportunity the vocabulary is the published
   * process. Otherwise it is what `describe` says the column may hold — which is the enum itself,
   * so a lead can be filtered to Converted before any lead has been converted. Only where the
   * server declares nothing are the rows the vocabulary, and a list of what happens to be on this
   * page is a filter that hides the value you were looking for.
   */
  const stages = useMemo(() => {
    if (model.stageField === undefined) {
      return []
    }

    if (process.data !== undefined) {
      return process.data.stages.map((stage) => stage.name)
    }

    if (declared.length > 0) {
      return declared
    }

    return [
      ...new Set(
        source
          .map((row) => String(row[model.stageField as string] ?? ''))
          .filter((value) => value !== ''),
      ),
    ].sort((left, right) => left.localeCompare(right))
  }, [declared, model.stageField, process.data, source])

  const rows = useMemo(() => {
    const term = search.trim().toLowerCase()

    return source.filter((record) => {
      if (stage !== 'all' && model.stageField && record[model.stageField] !== stage) return false
      if (term === '') return true
      // Every field, not just the first column: a list whose search only looked at the name is a
      // list you cannot use to find the account somebody mentioned on the telephone.
      return Object.values(record).some((value) => String(value ?? '').toLowerCase().includes(term))
    })
  }, [source, model.stageField, search, stage])

  const columns: readonly Column<RecordRow>[] = model.listCols
    .filter((name) => !hidden.includes(name))
    .map((name) => ({
      id: name,
      header: model.fields.find((field) => field.name === name)?.label ?? name,
      numeric: isNumeric(model, name),
      cell: (row: RecordRow) => renderCell(model, row, name),
      sortValue: (row: RecordRow) => {
        const value = row[name]
        return typeof value === 'number' ? value : String(value ?? '')
      },
    }))

  return (
    <Page layout="full">
      <PageHeader
        bar
        small
        eyebrow={
          `${rows.length} of ${source.length} · ` +
          (page.isPending
            ? 'reading…'
            : page.isError
              ? 'the server did not answer'
              // The object's own plural, not the server's kind with an "s" on it: the entity is
              // `Activity` and the screen is Tasks, and "live activitys" is neither.
              : `live ${model.plural.toLowerCase()}`)
        }
        title={model.plural}
        actions={
          <>
            <TextField
              label="Search this list"
              className={styles.search}
              placeholder={`Search ${model.plural.toLowerCase()}…`}
              value={search}
              onChange={(event) => setSearch(event.target.value)}
            />
            <Button onClick={() => setShowColumns((open) => !open)} aria-expanded={showColumns}>
              Columns
            </Button>
            {entity === 'Opportunity' ? (
              <Button onClick={() => void navigate({ to: '/kanban' })}>Kanban</Button>
            ) : null}
            {/*
              Only the two this build can actually write. A button that opened a form the server
              would refuse is worse than one that says it is not wired: the first wastes the
              reader's typing to tell them, the second tells them before they start.
            */}
            <Button
              tone="primary"
              disabled={!CAN_CREATE.includes(objectKey)}
              title={CAN_CREATE.includes(objectKey) ? undefined : WHY_NOT[objectKey]}
              onClick={() => setCreating(true)}
            >
              New {model.label.toLowerCase()}
            </Button>
          </>
        }
      />

      {stages.length > 0 ? (
        <div className={styles.filterStrip}>
          <ButtonGroup label={`Filter by ${model.stageField}`}>
            <Button size="sm" aria-pressed={stage === 'all'} onClick={() => setStage('all')}>
              All
            </Button>
            {stages.map((option) => (
              <Button
                key={option}
                size="sm"
                aria-pressed={stage === option}
                onClick={() => setStage(option)}
              >
                {option}
              </Button>
            ))}
          </ButtonGroup>
          <span className={styles.filterNote}>
            {stage === 'all' ? 'Every record in this view' : `Filtered to ${stage}`}
          </span>
        </div>
      ) : null}

      {showColumns ? (
        <div className={styles.columnPanel}>
          <span className={styles.columnLabel}>Columns</span>
          {model.listCols.map((name) => (
            <label key={name} className={styles.columnToggle}>
              <input
                type="checkbox"
                checked={!hidden.includes(name)}
                onChange={(event) =>
                  setHidden((current) =>
                    event.target.checked
                      ? current.filter((hiddenName) => hiddenName !== name)
                      : [...current, name],
                  )
                }
              />
              {model.fields.find((field) => field.name === name)?.label ?? name}
            </label>
          ))}
        </div>
      ) : null}

      <div className={styles.body}>
        {/*
          A refusal is shown, not swallowed under an empty table. "No accounts yet" is what a
          reader concludes from an empty list, and on a 403 that sentence is false in the one
          direction that matters — it says the tenant has none rather than that this caller may
          not see them.
        */}
        {page.isError ? <ErrorState error={page.error} onRetry={page.refetch} /> : null}

        <DataTable
          caption={`All ${model.plural.toLowerCase()}`}
          columns={columns}
          rows={rows}
          rowKey={(row) => row.id}
          onRowClick={setPeek}
          isRowSelected={(row) => row.id === peek?.id}
          empty={
            search
              ? `Nothing in ${model.plural.toLowerCase()} matches “${search}”.`
              : `No ${model.plural.toLowerCase()} yet.`
          }
        />
      </div>

      {creating && objectKey === 'lead' ? (
        <NewLeadDrawer onClose={() => setCreating(false)} />
      ) : null}

      {creating && objectKey === 'task' ? (
        <NewTaskDrawer onClose={() => setCreating(false)} />
      ) : null}

      {peek ? (
        <Drawer
          eyebrow={`${model.label} · ${peek.id}`}
          title={String(peek[model.listCols[0] ?? 'name'] ?? peek.id)}
          size={size}
          onSizeChange={setSize}
          onClose={() => setPeek(null)}
          actions={
            <Button
              tone="primary"
              onClick={() =>
                void navigate({
                  to: '/records/$object/$id',
                  params: { object: model.key, id: peek.id },
                })
              }
            >
              Open as page
            </Button>
          }
        >
          {/*
            THE PEEK IS THE RECORD PAGE, at a density. It used to be a second rendering — the
            fields off the model in one flat list, a stage strip made of tags, and its own Edit
            button — which is two places to add a field to and one of them gets forgotten. It also
            showed the *row* the list had already fetched rather than the record, so a column the
            list does not select was simply missing from the peek with nothing saying so.
          */}
          <RecordDetail
            objectKey={objectKey}
            id={peek.id}
            density={size === 'peek' ? 'peek' : 'full'}
            identity={false}
          />
        </Drawer>
      ) : null}

    </Page>
  )
}
