import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { AppAdmin } from './Admin.tsx'
import './layout.css'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <AppAdmin />
  </StrictMode>,
)
