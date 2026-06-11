import { Admin, Resource, combineDataProviders, defaultTheme } from 'react-admin'
import { createTheme } from '@mui/material/styles'
import People from '@mui/icons-material/People'
import Dashboard from '@mui/icons-material/Dashboard'
import PersonSearch from '@mui/icons-material/PersonSearch'
import ReportProblem from '@mui/icons-material/ReportProblem'
import MedicalServices from '@mui/icons-material/MedicalServices'
import { authProvider } from './authProvider'
import portalDataProvider from './dataProvider'
import workflowDataProvider from './workflowDataProvider'
import UserList from './users/UserList'
import CaseList from './cases/CaseList'
import CaseShow from './cases/CaseShow'
import PatientList from './patients/PatientList'
import ScrubReviewList from './scrub-review/ScrubReviewList'
import HumanReviewList from './human-review/HumanReviewList'
import Gavel from '@mui/icons-material/Gavel'
import DoctorReviewList from './doctor-view/DoctorReviewList'
import Login from './Login'

const LUGIANO_RED = '#E11D2A'

const theme = createTheme({
  ...defaultTheme,
  palette: {
    ...defaultTheme.palette,
    primary: { ...defaultTheme.palette?.primary, main: LUGIANO_RED },
    secondary: { ...defaultTheme.palette?.secondary, main: LUGIANO_RED },
  },
  components: {
    ...defaultTheme.components,
    // react-admin's Layout root ships with `min-width: fit-content`, so it grows
    // to its widest descendant (a wide datagrid, etc.) and forces a page-level
    // horizontal scrollbar instead of letting the content column shrink. Override
    // through the theme so it wins over react-admin's own Emotion styles, then let
    // `min-width: 0` on the content slot size it to the space left by the sidebar.
    // Genuinely wide content scrolls inside its own card (see the overflow
    // wrappers in the case cards).
    RaLayout: {
      styleOverrides: {
        root: { minWidth: 0 },
        content: { minWidth: 0 },
      },
    },
  },
})

// Route the .NET Workflow API resources (cases + live PSChiro lookup + scrub
// review queue) to the workflow data provider; everything else (users, etc.)
// to NestJS.
const WORKFLOW_RESOURCES = new Set(['cases', 'patients', 'scrub-review', 'human-review'])
const dataProvider = combineDataProviders((resource) =>
  WORKFLOW_RESOURCES.has(resource) ? workflowDataProvider : portalDataProvider,
)

export const AppAdmin = () => (
  <Admin
    dataProvider={dataProvider}
    authProvider={authProvider}
    theme={theme}
    loginPage={Login}
    title="Lugiano Portal"
  >
    {/* Live PSChiro lookup. No show route here — clicks navigate to /cases/{id}/show. */}
    <Resource
      name="patients"
      list={PatientList}
      icon={PersonSearch}
      options={{ label: 'Patients' }}
    />
    <Resource
      name="cases"
      list={CaseList}
      show={CaseShow}
      icon={Dashboard}
      options={{ label: 'Workflow' }}
    />
    {/* Failed-scrub triage queue. No show route — clicks navigate to /cases/{id}/show. */}
    <Resource
      name="scrub-review"
      list={ScrubReviewList}
      icon={ReportProblem}
      options={{ label: 'Doctor Queue' }}
    />
    <Resource
      name="human-review"
      list={HumanReviewList}
      icon={Gavel}
      options={{ label: 'Human Review' }}
    />
    {/* Doctor's queue. Demo-shortcut: same data as scrub-review, no real role
        scoping yet. The list reuses resource="scrub-review" but adds an
        in-place correction modal. */}
    <Resource
      name="doctor-view"
      list={DoctorReviewList}
      icon={MedicalServices}
      options={{ label: 'Doctor View' }}
    />
    <Resource name="users" list={UserList} icon={People} />
  </Admin>
)
