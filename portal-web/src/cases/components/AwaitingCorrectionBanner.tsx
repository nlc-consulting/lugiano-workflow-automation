import { useRecordContext } from 'react-admin'
import { Alert } from '@mui/material'
import ReplyAll from '@mui/icons-material/ReplyAll'

// Surfaces between the patient header and the rest of the page when the case
// is awaiting a doctor correction. Auto-clears when the next ChartNote arrives
// via sync.
const AwaitingCorrectionBanner = () => {
  const r = useRecordContext()
  if (r?.currentState !== 'AwaitingDoctorCorrection') return null
  return (
    <Alert severity="warning" variant="outlined" icon={<ReplyAll fontSize="small" />}>
      This case is awaiting a doctor correction. It will auto-resume when the
      doctor adds a new chart note in ChiroTouch.
    </Alert>
  )
}

export default AwaitingCorrectionBanner
