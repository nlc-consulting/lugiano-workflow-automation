import './workflowAuth' // patches window.fetch to attach the JWT to /workflow-api calls — must load first
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { AppAdmin } from './Admin.tsx'
import './layout.css'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <AppAdmin />
  </StrictMode>,
)
