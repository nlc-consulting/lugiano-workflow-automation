import { useState } from 'react'
import { Button, useNotify, useRecordContext, useRefresh, type RaRecord } from 'react-admin'
import CheckCircleOutline from '@mui/icons-material/CheckCircleOutline'

const WORKFLOW_API = import.meta.env.VITE_WORKFLOW_API_URL || '/workflow-api'

type CaseRecord = RaRecord & { pipVerified?: boolean }

// Portal-driven PIP verification: POSTs to the workflow service, which records a
// PipVerified event and advances the case state, then refreshes the list.
const VerifyPipButton = () => {
  const record = useRecordContext<CaseRecord>()
  const notify = useNotify()
  const refresh = useRefresh()
  const [loading, setLoading] = useState(false)

  // Nothing to do once it's verified.
  if (!record || record.pipVerified) return null

  const handleClick = async (e: React.MouseEvent) => {
    e.stopPropagation()
    setLoading(true)
    try {
      const res = await fetch(`${WORKFLOW_API}/cases/${record.id}/verify-pip`, {
        method: 'POST',
      })
      if (!res.ok) throw new Error(`HTTP ${res.status}`)
      notify('PIP verified', { type: 'success' })
      refresh()
    } catch {
      notify('Could not verify PIP', { type: 'error' })
    } finally {
      setLoading(false)
    }
  }

  return (
    <Button label="Verify PIP" onClick={handleClick} disabled={loading}>
      <CheckCircleOutline />
    </Button>
  )
}

export default VerifyPipButton
