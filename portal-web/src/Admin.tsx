import { Admin, Resource, defaultTheme } from 'react-admin'
import { createTheme } from '@mui/material/styles'
import People from '@mui/icons-material/People'
import { authProvider } from './authProvider'
import dataProvider from './dataProvider'
import UserList from './users/UserList'

// Biostar brand color carried over.
const theme = createTheme({
  ...defaultTheme,
  palette: {
    ...defaultTheme.palette,
    primary: { ...defaultTheme.palette?.primary, main: '#204487' },
    secondary: { ...defaultTheme.palette?.secondary, main: '#204487' },
  },
})

export const AppAdmin = () => (
  <Admin
    dataProvider={dataProvider}
    authProvider={authProvider}
    theme={theme}
    title="Lugiano Portal"
  >
    <Resource name="users" list={UserList} icon={People} />
  </Admin>
)
