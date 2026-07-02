import { useState } from 'react'
import { useNotify, useRefresh } from 'react-admin'
import {
  Alert,
  Box,
  Button,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Divider,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  Typography,
} from '@mui/material'
import Paid from '@mui/icons-material/Paid'

const WORKFLOW_API = import.meta.env.VITE_WORKFLOW_API_URL || '/workflow-api'

// Manual "bill now" trigger for a single visit. Hits /billing/visit which
// inserts BilledCharges rows for every unbilled service Transaction on the
// appointment — mirrors CT's paper-claim flow (one row per service, no
// ClaimLines linkage, matching 84% of CT prod rows).
//
// Billing writes to ChiroTouch and is hard to undo, so the click opens a
// confirmation dialog that first previews exactly which charges + total will be
// billed (GET /billing/visit/preview, read-only) before the operator commits.
type Props = {
  patientId: number
  visitId: number
}

type PreviewCharge = {
  id: number
  code?: string | null
  description?: string | null
  amount: number
}

type Preview = {
  count: number
  total: number
  charges: PreviewCharge[]
}

const moneyFmt = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' })

const BillNowButton = ({ patientId, visitId }: Props) => {
  const [open, setOpen] = useState(false)
  const [loading, setLoading] = useState(false)
  const [billing, setBilling] = useState(false)
  const [preview, setPreview] = useState<Preview | null>(null)
  const [error, setError] = useState<string | null>(null)
  const notify = useNotify()
  const refresh = useRefresh()

  const openDialog = async () => {
    setOpen(true)
    setPreview(null)
    setError(null)
    setLoading(true)
    try {
      const resp = await fetch(
        `${WORKFLOW_API}/billing/visit/preview?patientId=${patientId}&appointmentId=${visitId}`,
      )
      const body = await resp.json().catch(() => ({}))
      if (!resp.ok) throw new Error(body?.error || `HTTP ${resp.status}`)
      setPreview(body as Preview)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load charges')
    } finally {
      setLoading(false)
    }
  }

  const confirmBill = async () => {
    setBilling(true)
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
      setOpen(false)
      refresh()
    } catch (e) {
      notify(`Bill failed: ${e instanceof Error ? e.message : 'unknown error'}`, {
        type: 'error',
      })
    } finally {
      setBilling(false)
    }
  }

  const nothingToBill = !loading && !error && (preview?.count ?? 0) === 0

  return (
    <>
      <Button
        size="small"
        variant="outlined"
        color="primary"
        startIcon={<Paid fontSize="small" />}
        onClick={openDialog}
      >
        Bill now
      </Button>

      <Dialog open={open} onClose={() => !billing && setOpen(false)} maxWidth="sm" fullWidth>
        <DialogTitle>Confirm billing</DialogTitle>
        <DialogContent>
          {loading && (
            <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, py: 2 }}>
              <CircularProgress size={18} />
              <Typography variant="body2">Loading charges…</Typography>
            </Box>
          )}

          {error && <Alert severity="error">{error}</Alert>}

          {nothingToBill && (
            <Alert severity="info">
              No unbilled charges on this visit — they're already billed or none were entered.
            </Alert>
          )}

          {!loading && !error && (preview?.count ?? 0) > 0 && (
            <>
              <Typography variant="body2" sx={{ mb: 1.5 }}>
                The following {preview!.count} charge{preview!.count === 1 ? '' : 's'} will be
                marked <strong>billed in ChiroTouch</strong>. This can't be easily undone.
              </Typography>
              <Table size="small">
                <TableHead>
                  <TableRow>
                    <TableCell>Code</TableCell>
                    <TableCell>Description</TableCell>
                    <TableCell align="right">Amount</TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {preview!.charges.map((c) => (
                    <TableRow key={c.id}>
                      <TableCell>{c.code ?? '—'}</TableCell>
                      <TableCell>{c.description ?? '—'}</TableCell>
                      <TableCell align="right">{moneyFmt.format(c.amount)}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
              <Divider sx={{ my: 1 }} />
              <Box sx={{ display: 'flex', justifyContent: 'flex-end', gap: 2 }}>
                <Typography variant="subtitle1" sx={{ fontWeight: 700 }}>
                  Total: {moneyFmt.format(preview!.total)}
                </Typography>
              </Box>
            </>
          )}
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setOpen(false)} disabled={billing}>
            Cancel
          </Button>
          <Button
            variant="contained"
            color="primary"
            onClick={confirmBill}
            disabled={billing || loading || nothingToBill || !!error}
            startIcon={billing ? <CircularProgress size={14} /> : <Paid fontSize="small" />}
          >
            {billing ? 'Billing…' : 'Confirm & bill'}
          </Button>
        </DialogActions>
      </Dialog>
    </>
  )
}

export default BillNowButton
