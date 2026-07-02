import { Admin, Resource, combineDataProviders, defaultTheme } from 'react-admin'
import { createTheme } from '@mui/material/styles'
import People from '@mui/icons-material/People'
import Dashboard from '@mui/icons-material/Dashboard'
import PersonSearch from '@mui/icons-material/PersonSearch'
import MedicalServices from '@mui/icons-material/MedicalServices'
import Receipt from '@mui/icons-material/Receipt'
import { authProvider } from './authProvider'
import portalDataProvider from './dataProvider'
import workflowDataProvider from './workflowDataProvider'
import UserList from './users/UserList'
import CaseList from './cases/CaseList'
import CaseShow from './cases/CaseShow'
import PatientList from './patients/PatientList'
import HumanReviewList from './human-review/HumanReviewList'
import Gavel from '@mui/icons-material/Gavel'
import DoctorReviewList from './doctor-view/DoctorReviewList'
// Legacy EOB import/preview page — hidden from the sidebar for now but kept
// so we can re-enable it once the scan-parser output flows into the same
// matching UI as an alternate source. See the commented Resource below.
// eslint-disable-next-line @typescript-eslint/no-unused-vars
import EobPreview from './eob/EobPreview'
import EobScan from './eob/EobScan'
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
    {/* Role-based access. The JWT carries a `role` claim (authProvider
        getPermissions). Gating:
          admin   → everything
          billing → Workflow only
          doctor  → Doctor View only (their kicked-back notes)
        Unknown/missing role falls back to admin so existing logins aren't
        locked out before roles are assigned on the backend — tighten once
        every account issues an explicit role. */}
    {(permissions) => {
      // Backend issues UPPERCASE UserRole enum values (ADMIN / BILLER /
      // DOCTOR / ATTORNEY) — normalize before comparing, and match the real
      // enum name (BILLER, not "billing").
      const role = ((permissions as string | null) ?? 'admin').toLowerCase()
      const isAdmin = role === 'admin'
      const isBilling = role === 'biller'
      const isDoctor = role === 'doctor'
      const canWorkflow = isAdmin || isBilling
      const canDoctorView = isAdmin || isDoctor

      return (
        <>
          {canWorkflow && (
            <Resource
              name="cases"
              list={CaseList}
              show={CaseShow}
              icon={Dashboard}
              options={{ label: 'Workflow' }}
            />
          )}
          {canDoctorView && (
            <Resource
              name="doctor-view"
              list={DoctorReviewList}
              icon={MedicalServices}
              options={{ label: 'Doctor View' }}
            />
          )}
          {isAdmin && (
            <Resource
              name="human-review"
              list={HumanReviewList}
              icon={Gavel}
              options={{ label: 'Human Review' }}
            />
          )}
          {(isAdmin || isBilling) && (
            <Resource
              name="patients"
              list={PatientList}
              icon={PersonSearch}
              options={{ label: 'Patients' }}
            />
          )}
          {isAdmin && (
            <Resource
              name="eob-scan"
              list={EobScan}
              icon={Receipt}
              options={{ label: 'EOB Scan' }}
            />
          )}
          {/* HIDDEN — legacy vendor-xlsx import + preview flow. File kept in
              src/eob/EobPreview.tsx for later re-enable once the scan-parser
              output can feed into it as an alternate source. Restore by
              uncommenting the Resource block below:
              {isAdmin && (
                <Resource name="eob" list={EobPreview} icon={Receipt} options={{ label: 'EOB' }} />
              )} */}
          {/* Hidden routing resource — kept registered wherever Doctor View is
              shown so the workflowDataProvider still routes scrub-review fetches. */}
          {canDoctorView && <Resource name="scrub-review" />}
          {isAdmin && <Resource name="users" list={UserList} icon={People} />}
        </>
      )
    }}
  </Admin>
)
