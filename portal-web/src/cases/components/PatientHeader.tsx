import { useRecordContext } from 'react-admin'
import { Box, Button, Card, CardContent, Chip, Typography } from '@mui/material'
import PictureAsPdf from '@mui/icons-material/PictureAsPdf'

const WORKFLOW_API = import.meta.env.VITE_WORKFLOW_API_URL || '/workflow-api'

// Workflow state → label + MUI chip color. Single place to extend when new
// states land (scrub-related ones in the next epic).
const stateColor = (state?: string): 'error' | 'warning' | 'success' | 'default' => {
  switch (state) {
    case 'AwaitingInsurance':
      return 'error'
    case 'AwaitingPipVerification':
    case 'AwaitingDoctorNotes':
    case 'AwaitingDoctorCorrection':
    case 'AwaitingCharges':
      return 'warning'
    case 'ReadyForAiScrubbing':
    case 'ReadyForBilling':
      return 'success'
    default:
      return 'default'
  }
}

const stateLabel = (state?: string): string => {
  switch (state) {
    case 'AwaitingInsurance':
      return 'Awaiting insurance'
    case 'AwaitingPipVerification':
      return 'Awaiting PIP verification'
    case 'AwaitingDoctorNotes':
      return 'Awaiting doctor notes'
    case 'AwaitingDoctorCorrection':
      return 'Awaiting doctor correction'
    case 'AwaitingCharges':
      return 'Awaiting charges'
    case 'ReadyForAiScrubbing':
      return 'Ready for AI scrubbing'
    case 'ReadyForBilling':
      return 'Ready for billing'
    default:
      return state ?? '—'
  }
}

const PatientHeader = () => {
  const r = useRecordContext()
  if (!r) return null
  const name = [r.firstName, r.middleName, r.lastName].filter(Boolean).join(' ')
  const address = [r.address, r.city, r.state, r.zip].filter(Boolean).join(', ')
  // Active case marker mirrors ChiroTouch's "(Auto)" style next to the
  // patient name. caseType + curInjuryDate come straight from
  // Patients.CaseType / Patients.CurInjuryDate — patient-level case anchor
  // (free-text values like "Auto", "Auto 2", "Slip/Fall", "WC").
  const caseType = (r.caseType as string | null | undefined)?.trim() || null
  const injuryDate = r.curInjuryDate
    ? new Date(r.curInjuryDate as string).toLocaleDateString('en-US', {
        timeZone: 'America/New_York',
      })
    : null
  return (
    <Card>
      <CardContent>
        <Box
          sx={{
            display: 'flex',
            justifyContent: 'space-between',
            alignItems: 'flex-start',
            gap: 2,
          }}
        >
          <Box>
            <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, flexWrap: 'wrap' }}>
              <Typography variant="h5" sx={{ fontWeight: 600 }}>
                {name || `Patient ${r.patientId}`}
              </Typography>
              {caseType && (
                <Chip
                  size="small"
                  color="primary"
                  variant="outlined"
                  label={caseType}
                  sx={{ fontWeight: 600 }}
                />
              )}
            </Box>
            <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
              {/* AccountNo is the team-facing ID — bolded so it anchors the
                  identifier line. Internal Patients.ID kept alongside for
                  developers / URL traceability. */}
              {r.accountNo != null && (
                <>
                  <Box component="span" sx={{ fontWeight: 700, color: 'text.primary' }}>
                    Acct #{r.accountNo}
                  </Box>
                  {' · '}
                </>
              )}
              ID {r.patientId} · {r.sex ?? '—'} · {r.primaryDoctor ?? '—'}
              {injuryDate && <> · accident {injuryDate}</>}
            </Typography>
            {address && (
              <Typography variant="body2" color="text.secondary">
                {address}
              </Typography>
            )}
          </Box>
          {/* Notes PDF button removed — the per-DOS HCFA endpoint now bundles
              the chart note alongside the form. The standalone /notes/preview
              endpoint is still wired in case a full-history PDF is needed. */}
          <Chip
            color={stateColor(r.currentState)}
            label={stateLabel(r.currentState)}
            sx={{ fontWeight: 600 }}
          />
        </Box>
      </CardContent>
    </Card>
  )
}

export default PatientHeader
