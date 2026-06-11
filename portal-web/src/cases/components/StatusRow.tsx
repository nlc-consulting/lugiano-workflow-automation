import { useRecordContext } from 'react-admin'
import { Box, Card, CardContent, Chip, Typography } from '@mui/material'
import CheckCircle from '@mui/icons-material/CheckCircle'
import RadioButtonUnchecked from '@mui/icons-material/RadioButtonUnchecked'
import { formatShortDate } from './formatters'
// PIP is hidden from the UI for the time being — not part of the billing
// critical path. Restore by re-importing PipDateEditor and re-adding the
// PIP pill + editor mount below.

type StatusPillProps = {
  label: string
  present: boolean
  secondary?: string | null
}

const StatusPill = ({ label, present, secondary }: StatusPillProps) => (
  <Chip
    color={present ? 'success' : 'default'}
    variant={present ? 'filled' : 'outlined'}
    icon={present ? <CheckCircle /> : <RadioButtonUnchecked />}
    label={
      <Box component="span" sx={{ display: 'inline-flex', alignItems: 'baseline', gap: 0.75 }}>
        <Box component="span" sx={{ fontWeight: 600 }}>
          {label}
        </Box>
        {secondary && (
          <Box component="span" sx={{ opacity: 0.85, fontSize: '0.85em' }}>
            · {secondary}
          </Box>
        )}
      </Box>
    }
  />
)

const StatusRow = () => {
  const r = useRecordContext()
  if (!r) return null
  return (
    <Card>
      <CardContent>
        <Typography variant="overline" color="text.secondary" sx={{ display: 'block', mb: 1 }}>
          Billing readiness
        </Typography>
        <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1.5, alignItems: 'center' }}>
          <StatusPill
            label="Insurance"
            present={!!r.insuranceProvided}
            secondary={
              r.insuranceAddedAt ? `effective ${formatShortDate(r.insuranceAddedAt)}` : 'no policy on file'
            }
          />
          <StatusPill
            label="Doctor notes"
            present={!!r.doctorNotesReceived}
            secondary={
              r.doctorNotesReceivedAt ? `latest ${formatShortDate(r.doctorNotesReceivedAt)}` : 'none yet'
            }
          />
        </Box>
      </CardContent>
    </Card>
  )
}

export default StatusRow
