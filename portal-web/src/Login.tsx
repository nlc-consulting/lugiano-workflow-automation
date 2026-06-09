import { LoginForm, Notification } from 'react-admin'
import { Box, Card, CardContent, Typography } from '@mui/material'

// Branded login page. Red gradient sits behind a centered card that wraps
// react-admin's standard LoginForm — same auth flow, just our chrome.
const Login = () => (
  <Box
    sx={{
      minHeight: '100vh',
      display: 'flex',
      alignItems: 'center',
      justifyContent: 'center',
      px: 2,
      background: 'linear-gradient(135deg, #E11D2A 0%, #7A0F18 100%)',
    }}
  >
    <Card sx={{ width: '100%', maxWidth: 400, borderRadius: 2, boxShadow: 8 }}>
      <CardContent sx={{ p: 4 }}>
        <Box sx={{ textAlign: 'center', mb: 3 }}>
          <Typography variant="h4" sx={{ fontWeight: 700, color: '#E11D2A', letterSpacing: 1 }}>
            LUGIANO
          </Typography>
          <Typography variant="overline" color="text.secondary" sx={{ letterSpacing: 2 }}>
            Medical Billing Portal
          </Typography>
        </Box>
        <LoginForm />
      </CardContent>
    </Card>
    <Notification />
  </Box>
)

export default Login
