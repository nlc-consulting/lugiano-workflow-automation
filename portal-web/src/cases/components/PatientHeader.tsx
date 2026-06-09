import { useRecordContext } from 'react-admin'
import { Box, Card, CardContent, Chip, Typography } from '@mui/material'

// Workflow state → label + MUI chip color. Single place to extend when new
// states land (scrub-related ones in the next epic).
const stateColor = (state?: string): 'error' | 'warning' | 'success' | 'default' => {
  switch (state) {
    case 'AwaitingInsurance':
      return 'error'
    case 'AwaitingPipVerification':
    case 'AwaitingDoctorNotes':
    case 'AwaitingDoctorCorrection':
      return 'warning'
    case 'ReadyForAiScrubbing':
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
    case 'ReadyForAiScrubbing':
      return 'Ready for AI scrubbing'
    default:
      return state ?? '—'
  }
}

const PatientHeader = () => {
  const r = useRecordContext()
  if (!r) return null
  const name = [r.firstName, r.middleName, r.lastName].filter(Boolean).join(' ')
  const address = [r.address, r.city, r.state, r.zip].filter(Boolean).join(', ')
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
            <Typography variant="h5" sx={{ fontWeight: 600 }}>
              {name || `Patient ${r.patientId}`}
            </Typography>
            <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
              ID {r.patientId} · {r.sex ?? '—'} · {r.primaryDoctor ?? '—'}
            </Typography>
            {address && (
              <Typography variant="body2" color="text.secondary">
                {address}
              </Typography>
            )}
          </Box>
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
