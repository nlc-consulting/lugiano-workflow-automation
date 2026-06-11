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

type CaseRecord = RaRecord & {
  firstName?: string
  lastName?: string
  latestScrubVerdict?: 'pass' | 'needs_review' | 'fail' | null
  latestScrubAt?: string | null
  outstandingChargesCount?: number
  outstandingChargesTotal?: number
  oldestOutstandingChargeDate?: string | null
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

// Outstanding-charges chip — the third billing-readiness gate. Shows the $
// total only; the underlying "what visits/lines" lives on the case detail.
// Reviewers scan the column to find cases ready to bill; muted "All billed"
// quietly drops fully-billed cases off the eye line.
const OutstandingChargesChip = ({ row }: { row: CaseRecord }) => {
  const count = row.outstandingChargesCount ?? 0
  if (count === 0) {
    return (
      <Chip size="small" variant="outlined" label="All billed" sx={{ opacity: 0.65 }} />
    )
  }
  const total = row.outstandingChargesTotal ?? 0
  const oldest = row.oldestOutstandingChargeDate
    ? new Date(row.oldestOutstandingChargeDate).toLocaleDateString('en-US', {
        timeZone: 'America/New_York',
      })
    : null
  const tooltip = oldest ? `Oldest unbilled charge: ${oldest}` : ''
  return (
    <Tooltip title={tooltip} arrow>
      <Chip
        size="small"
        color="primary"
        variant="outlined"
        label={moneyFmt.format(total)}
        sx={{ fontWeight: 600 }}
      />
    </Tooltip>
  )
}

// Case-level scrub status. One verdict per case — no coverage math, no
// partial state. Either the case has been scrubbed or it hasn't, and if it
// has the verdict is the whole-bundle judgment.
const ScrubStatusChip = ({ row }: { row: CaseRecord }) => {
  if (!row.latestScrubVerdict) {
    return <Chip size="small" variant="outlined" label="Not scrubbed" />
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
      <BooleanField source="doctorNotesReceived" label="Doctor Notes" />
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
