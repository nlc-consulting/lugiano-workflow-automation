import { useState } from 'react'
import { useNotify, useRefresh } from 'react-admin'
import { Button, CircularProgress } from '@mui/material'
import Paid from '@mui/icons-material/Paid'

const WORKFLOW_API = import.meta.env.VITE_WORKFLOW_API_URL || '/workflow-api'

// Manual "bill now" trigger for a single visit. Hits /billing/visit which
// inserts BilledCharges rows for every unbilled service Transaction on the
// appointment — mirrors CT's paper-claim flow (one row per service, no
// ClaimLines linkage, matching 84% of CT prod rows).
//
// Lives on the Notes card next to Generate HCFA so the operator can run the
// post-fax sequence in one place: Generate HCFA -> Fax now -> Bill now.
// Same code path will run automatically once the fax-delivery webhook lands.
type Props = {
  patientId: number
  visitId: number
}

const BillNowButton = ({ patientId, visitId }: Props) => {
  const [busy, setBusy] = useState(false)
  const notify = useNotify()
  const refresh = useRefresh()

  const bill = async () => {
    setBusy(true)
    try {
      const resp = await fetch(
        `${WORKFLOW_API}/billing/visit?patientId=${patientId}&appointmentId=${visitId}`,
        { method: 'POST' },
      )
      const body = await resp.json().catch(() => ({}))
      if (!resp.ok) throw new Error(body?.error || `HTTP ${resp.status}`)
      const count = body?.count ?? 0
      notify(
        `Billed ${count} charge${count === 1 ? '' : 's'} (InsPolID ${body?.insPolId ?? '?'})`,
        { type: 'success' },
      )
      refresh()
    } catch (e) {
      notify(`Bill failed: ${e instanceof Error ? e.message : 'unknown error'}`, {
        type: 'error',
      })
    } finally {
      setBusy(false)
    }
  }

  return (
    <Button
      size="small"
      variant="outlined"
      color="primary"
      disabled={busy}
      startIcon={busy ? <CircularProgress size={14} /> : <Paid fontSize="small" />}
      onClick={bill}
    >
      {busy ? 'Billing…' : 'Bill now'}
    </Button>
  )
}

export default BillNowButton
