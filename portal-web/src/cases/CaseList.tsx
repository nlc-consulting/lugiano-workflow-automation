import { useEffect, useState, type MouseEvent } from 'react'
import {
  BooleanField,
  Datagrid,
  FunctionField,
  List,
  SelectInput,
  TextField,
  useNotify,
  useRefresh,
  type RaRecord,
} from 'react-admin'
import { Alert, Box, Button, Chip, CircularProgress, Stack, Tooltip, Typography } from '@mui/material'
import PlayArrow from '@mui/icons-material/PlayArrow'

const WORKFLOW_API = import.meta.env.VITE_WORKFLOW_API_URL || '/workflow-api'
// PIP verify is parked while we sort out the gating policy — see WorkflowConstants.
// Re-enable by restoring the import and the <VerifyPipButton /> column below.
// import VerifyPipButton from './VerifyPipButton'
import EstDateTimeField from './EstDateTimeField'
import { formatNoteStamp } from './components/formatters'

type CaseRecord = RaRecord & {
  firstName?: string
  lastName?: string
  latestScrubVerdict?: 'pass' | 'needs_review' | 'fail' | null
  latestScrubAt?: string | null
  outstandingChargesCount?: number
  outstandingChargesTotal?: number
  oldestOutstandingChargeDate?: string | null
  lastNoteDate?: string | null
  latestDoctorNoteId?: number | null
  accountNo?: number | null
  office?: string | null
}

// Canonical office labels — must match OfficeResolver on the backend.
const OFFICES = [
  'Center City',
  'PA Pain & Rehab (Main)',
  'North Broad',
  'Woodland',
  'South Philadelphia',
  'Lebanon Avenue',
  'Other / Unassigned',
]

// Office filter shown above the table. alwaysOn so it's visible without opening
// a filter menu; combined with filterDefaultValues it boots up scoped to
// Center City. Clearing it (empty choice) shows all offices.
const caseFilters = [
  <SelectInput
    key="office"
    source="office"
    label="Office"
    alwaysOn
    // Relabel the empty option so clearing the filter reads "All offices"
    // (and shows every office) instead of a blank/floaty state. An empty
    // value omits the filter on the wire, so the backend returns all rows.
    emptyText="All offices"
    choices={OFFICES.map((o) => ({ id: o, name: o }))}
  />,
]

const SCRUB_LABELS: Record<string, string> = {
  pass: 'Pass',
  needs_review: 'Needs review',
  fail: 'Fail',
}

const SCRUB_COLORS: Record<string, 'success' | 'warning' | 'error'> = {
  pass: 'success',
  needs_review: 'warning',
  fail: 'error',
}

const moneyFmt = new Intl.NumberFormat('en-US', {
  style: 'currency',
  currency: 'USD',
  maximumFractionDigits: 0,
})

// Insurance balance chip — what insurance still owes us (unbilled + AR).
// "$0" with muted styling = fully paid (rare for active Auto cases). A
// dollar amount = collectible work, whether it's net-new (still needs a
// claim sent) or follow-up (already on a claim, EOB pending). The unbilled
// count + oldest-charge date ride in the tooltip so reviewers can tell the
// two apart without leaving the dashboard.
const OutstandingChargesChip = ({ row }: { row: CaseRecord }) => {
  const balance = row.outstandingChargesTotal ?? 0
  const unbilledCount = row.outstandingChargesCount ?? 0
  if (balance === 0) {
    return (
      <Chip size="small" variant="outlined" label="$0" sx={{ opacity: 0.5 }} />
    )
  }
  const oldest = row.oldestOutstandingChargeDate
    ? new Date(row.oldestOutstandingChargeDate).toLocaleDateString('en-US', {
        timeZone: 'America/New_York',
      })
    : null
  const tooltipParts: string[] = []
  if (unbilledCount > 0) tooltipParts.push(`${unbilledCount} unbilled charge${unbilledCount === 1 ? '' : 's'}`)
  if (oldest && unbilledCount > 0) tooltipParts.push(`oldest ${oldest}`)
  if (unbilledCount === 0) tooltipParts.push('All claims sent — AR with insurance')
  return (
    <Tooltip title={tooltipParts.join(' · ')} arrow>
      <Chip
        size="small"
        color="primary"
        variant="outlined"
        label={moneyFmt.format(balance)}
        sx={{ fontWeight: 600 }}
      />
    </Tooltip>
  )
}

// Case-level scrub status. One verdict per case — no coverage math, no
// partial state. Either the case has been scrubbed or it hasn't, and if it
// has the verdict is the whole-bundle judgment. Unscrubbed cases read as
// "Scrub pending" with a manual "Scrub now" trigger so the demo can run
// one-off scrubs without flipping the AutoScrub config back on (which would
// burn the Claude budget across every pending case).
const ScrubStatusChip = ({ row }: { row: CaseRecord }) => {
  const [running, setRunning] = useState(false)
  const notify = useNotify()
  const refresh = useRefresh()

  if (!row.latestScrubVerdict) {
    const noteId = row.latestDoctorNoteId
    const handleClick = async (e: MouseEvent<HTMLButtonElement>) => {
      // Datagrid wraps the cell in a click-to-show handler; without this the
      // button doubles as a row navigation.
      e.stopPropagation()
      if (noteId == null || running) return
      setRunning(true)
      try {
        const resp = await fetch(`${WORKFLOW_API}/notes/${noteId}/scrub`, {
          method: 'POST',
        })
        if (!resp.ok) throw new Error(`HTTP ${resp.status}`)
        notify('Scrub complete', { type: 'success' })
        refresh()
      } catch (err) {
        notify(`Scrub failed: ${err instanceof Error ? err.message : 'unknown error'}`, {
          type: 'error',
        })
      } finally {
        setRunning(false)
      }
    }
    return (
      <Stack
        direction="row"
        spacing={1}
        alignItems="center"
        // nowrap so the chip + button never wrap into adjacent columns.
        sx={{ flexWrap: 'nowrap' }}
        // Belt-and-braces: any click inside this cell that isn't the chip
        // text itself should stop bubbling so the row-click never fires
        // when the user is reaching for the button.
        onClick={(e) => e.stopPropagation()}
      >
        <Chip size="small" variant="outlined" label="Scrub pending" sx={{ flexShrink: 0 }} />
        {noteId != null && (
          <Tooltip title="Run a one-off scrub on the latest note" arrow>
            <span>
              <Button
                size="small"
                variant="outlined"
                color="primary"
                disabled={running}
                onClick={handleClick}
                startIcon={
                  running ? (
                    <CircularProgress size={12} />
                  ) : (
                    <PlayArrow fontSize="small" />
                  )
                }
                // whiteSpace:nowrap keeps "Scrub now" on one line; flexShrink:0
                // stops the button collapsing and breaking the label.
                sx={{ whiteSpace: 'nowrap', flexShrink: 0, py: 0.25, px: 1, textTransform: 'none' }}
              >
                {running ? 'Scrubbing…' : 'Scrub now'}
              </Button>
            </span>
          </Tooltip>
        )}
      </Stack>
    )
  }
  const tooltip = row.latestScrubAt
    ? `Latest run: ${new Date(row.latestScrubAt).toLocaleString('en-US', {
        timeZone: 'America/New_York',
      })}`
    : ''
  return (
    <Tooltip title={tooltip} arrow>
      <Chip
        size="small"
        color={SCRUB_COLORS[row.latestScrubVerdict] ?? 'default'}
        label={SCRUB_LABELS[row.latestScrubVerdict] ?? row.latestScrubVerdict}
        sx={{ fontWeight: 600 }}
      />
    </Tooltip>
  )
}

// Polls /scrubbing/config once on mount. Renders an info banner when the
// AutoScrub kill-switch is off so reviewers know they need to fire scrubs
// manually via the per-row "Scrub now" button. Quiet otherwise.
const AutoScrubBanner = () => {
  const [autoScrub, setAutoScrub] = useState<boolean | null>(null)
  useEffect(() => {
    fetch(`${WORKFLOW_API}/scrubbing/config`)
      .then((r) => (r.ok ? r.json() : null))
      .then((j) => setAutoScrub(j?.autoScrub ?? null))
      .catch(() => setAutoScrub(null))
  }, [])
  if (autoScrub !== false) return null
  return (
    <Alert severity="info" variant="outlined" sx={{ mb: 1 }}>
      <strong>Auto-scrubbing is off.</strong> Cases stay in "Scrub pending" until
      someone fires them — use the <em>Scrub now</em> button on a row to run one
      manually.
    </Alert>
  )
}

const CaseList = () => (
  <List
    title="Patients in Workflow"
    sort={{ field: 'lastUpdatedAt', order: 'DESC' }}
    filters={caseFilters}
    filterDefaultValues={{ office: 'Center City' }}
  >
    <AutoScrubBanner />
    <Datagrid rowClick="show" bulkActionButtons={false}>
      {/* AccountNo is what the team identifies patients by — bold to anchor
          the row. The internal Patients.ID stays alongside (dimmed) so URL
          links keep working while everyone learns the new layout. */}
      <FunctionField
        label="Account #"
        sortBy="accountNo"
        render={(r: CaseRecord) =>
          r.accountNo != null ? (
            <Typography component="span" sx={{ fontWeight: 700 }}>
              {r.accountNo}
            </Typography>
          ) : (
            <Typography component="span" color="text.disabled">
              —
            </Typography>
          )
        }
      />
      <TextField source="patientId" label="Patient ID" />
      <FunctionField
        label="Patient"
        render={(r: CaseRecord) => `${r.firstName ?? ''} ${r.lastName ?? ''}`.trim()}
      />
      <TextField source="office" label="Office" />
      <BooleanField source="insuranceProvided" label="Insurance" />
      {/* Clinical date — render without time component so a midnight-UTC date
          doesn't shift to "previous day 8 PM EDT". */}
      <FunctionField
        label="Last note"
        sortBy="lastNoteDate"
        render={(r: CaseRecord) => formatNoteStamp(r.lastNoteDate)}
      />
      {/* PIP verified column parked alongside the action button — re-enable together. */}
      {/* <BooleanField source="pipVerified" label="PIP Verified" /> */}
      <FunctionField
        label="Scrub"
        render={(r: CaseRecord) => <ScrubStatusChip row={r} />}
      />
      <FunctionField
        label="To bill"
        render={(r: CaseRecord) => <OutstandingChargesChip row={r} />}
      />
      <TextField source="currentState" label="State" />
      <EstDateTimeField source="addedAt" label="Added" />
      <EstDateTimeField source="lastUpdatedAt" label="Updated" />
      {/* <VerifyPipButton /> */}
    </Datagrid>
  </List>
)

export default CaseList
