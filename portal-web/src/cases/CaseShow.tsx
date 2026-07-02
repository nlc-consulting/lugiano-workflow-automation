import { Button, Show, TopToolbar } from 'react-admin'
import { Box } from '@mui/material'
import ArrowBack from '@mui/icons-material/ArrowBack'
import { useNavigate } from 'react-router'
import PatientHeader from './components/PatientHeader'
import AwaitingCorrectionBanner from './components/AwaitingCorrectionBanner'
import StatusRow from './components/StatusRow'
import InsurancePoliciesCard from './components/InsurancePoliciesCard'
import NotesAndDiagnosesCard from './components/NotesAndDiagnosesCard'
import ChargesCard from './components/ChargesCard'
import FooterMeta from './components/FooterMeta'
import CaseScrubCard from './components/CaseScrubCard'
import TracerCard from './components/TracerCard'

// Browser-back so the button returns to whichever list the user came from
// (Patients lookup or the Automation queue) — react-admin's ListButton would
// always send them back to /cases regardless of origin.
const ShowActions = () => {
  const navigate = useNavigate()
  return (
    <TopToolbar>
      <Button label="Back" startIcon={<ArrowBack />} onClick={() => navigate(-1)} />
    </TopToolbar>
  )
}

// Once the scrubbing UI lands, this stack is a candidate for a top-level
// Tabs layout (Summary / Notes / Ledger / Scrubbing).
const CaseShowContent = () => (
  <Box
    sx={{
      display: 'flex',
      flexDirection: 'column',
      gap: 2,
      my: 1,
      // Keep the stack inside the content column: flex/grid children won't
      // shrink below their intrinsic content width without minWidth:0, which
      // is what lets wide tables push a horizontal scrollbar onto the page.
      width: '100%',
      minWidth: 0,
      maxWidth: '100%',
    }}
  >
    <PatientHeader />
    <AwaitingCorrectionBanner />
    <StatusRow />
    <InsurancePoliciesCard />
    <CaseScrubCard />
    <NotesAndDiagnosesCard />
    <TracerCard />
    <ChargesCard />
    <FooterMeta />
  </Box>
)

const CaseShow = () => (
  <Show title="Patient detail" actions={<ShowActions />}>
    <CaseShowContent />
  </Show>
)

export default CaseShow
