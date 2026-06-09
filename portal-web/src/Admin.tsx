import { Admin, Resource, combineDataProviders, defaultTheme } from 'react-admin'
import { createTheme } from '@mui/material/styles'
import People from '@mui/icons-material/People'
import Dashboard from '@mui/icons-material/Dashboard'
import { authProvider } from './authProvider'
import portalDataProvider from './dataProvider'
import workflowDataProvider from './workflowDataProvider'
import UserList from './users/UserList'
import CaseList from './cases/CaseList'
import CaseShow from './cases/CaseShow'
import Login from './Login'

const LUGIANO_RED = '#E11D2A'

const theme = createTheme({
  ...defaultTheme,
  palette: {
    ...defaultTheme.palette,
    primary: { ...defaultTheme.palette?.primary, main: LUGIANO_RED },
    secondary: { ...defaultTheme.palette?.secondary, main: LUGIANO_RED },
  },
})

// Route the 'cases' resource to the .NET Workflow API; everything else to NestJS.
const dataProvider = combineDataProviders((resource) =>
  resource === 'cases' ? workflowDataProvider : portalDataProvider,
)

export const AppAdmin = () => (
  <Admin
    dataProvider={dataProvider}
    authProvider={authProvider}
    theme={theme}
    loginPage={Login}
    title="Lugiano Portal"
  >
    <Resource
      name="cases"
      list={CaseList}
      show={CaseShow}
      icon={Dashboard}
      options={{ label: 'Patients' }}
    />
    <Resource name="users" list={UserList} icon={People} />
  </Admin>
)
