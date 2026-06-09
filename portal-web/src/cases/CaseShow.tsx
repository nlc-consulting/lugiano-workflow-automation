import { ListButton, Show, TopToolbar } from 'react-admin'
import { Box } from '@mui/material'
import ArrowBack from '@mui/icons-material/ArrowBack'
import PatientHeader from './components/PatientHeader'
import AwaitingCorrectionBanner from './components/AwaitingCorrectionBanner'
import StatusRow from './components/StatusRow'
import InsurancePoliciesCard from './components/InsurancePoliciesCard'
import NotesAndDiagnosesCard from './components/NotesAndDiagnosesCard'
import ChargesCard from './components/ChargesCard'
import FooterMeta from './components/FooterMeta'

const ShowActions = () => (
  <TopToolbar>
    <ListButton label="Back to patients" icon={<ArrowBack />} />
  </TopToolbar>
)

// Once the scrubbing UI lands, this stack is a candidate for a top-level
// Tabs layout (Summary / Notes / Ledger / Scrubbing).
const CaseShowContent = () => (
  <Box sx={{ display: 'flex', flexDirection: 'column', gap: 2, my: 1 }}>
    <PatientHeader />
    <AwaitingCorrectionBanner />
    <StatusRow />
    <InsurancePoliciesCard />
    <NotesAndDiagnosesCard />
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
