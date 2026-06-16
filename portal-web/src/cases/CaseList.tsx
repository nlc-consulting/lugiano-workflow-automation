import {
  BooleanField,
  Datagrid,
  FunctionField,
  List,
  TextField,
  type RaRecord,
} from 'react-admin'
import { Box, Chip, Tooltip, Typography } from '@mui/material'
// PIP verify is parked while we sort out the gating policy — see WorkflowConstants.
// Re-enable by restoring the import and the <VerifyPipButton /> column below.
// import VerifyPipButton from './VerifyPipButton'
import EstDateTimeField from './EstDateTimeField'
import { formatShortDate } from './components/formatters'

type CaseRecord = RaRecord & {
  firstName?: string
  lastName?: string
  latestScrubVerdict?: 'pass' | 'needs_review' | 'fail' | null
  latestScrubAt?: string | null
  outstandingChargesCount?: number
  outstandingChargesTotal?: number
  oldestOutstandingChargeDate?: string | null
  lastNoteDate?: string | null
}

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
// "Scrub pending" since auto-scrub is wired — the absence of a verdict
// means the run hasn't completed yet, not that someone forgot.
const ScrubStatusChip = ({ row }: { row: CaseRecord }) => {
  if (!row.latestScrubVerdict) {
    return <Chip size="small" variant="outlined" label="Scrub pending" />
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

const CaseList = () => (
  <List title="Patients in Workflow" sort={{ field: 'lastUpdatedAt', order: 'DESC' }}>
    <Datagrid rowClick="show" bulkActionButtons={false}>
      <TextField source="patientId" label="Patient ID" />
      <FunctionField
        label="Patient"
        render={(r: CaseRecord) => `${r.firstName ?? ''} ${r.lastName ?? ''}`.trim()}
      />
      <BooleanField source="insuranceProvided" label="Insurance" />
      {/* Clinical date — render without time component so a midnight-UTC date
          doesn't shift to "previous day 8 PM EDT". */}
      <FunctionField
        label="Last note"
        sortBy="lastNoteDate"
        render={(r: CaseRecord) => formatShortDate(r.lastNoteDate) ?? '—'}
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
