import { useEffect, useMemo, useState } from 'react'
import { Title } from 'react-admin'
import {
  Alert,
  Box,
  Button,
  Card,
  CardContent,
  Checkbox,
  CircularProgress,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  TextField,
  Typography,
} from '@mui/material'
import PictureAsPdf from '@mui/icons-material/PictureAsPdf'
import { withWorkflowToken } from '../apiClient'

const WORKFLOW_API = import.meta.env.VITE_WORKFLOW_API_URL || '/workflow-api'

type Batch = {
  id: string
  patientId: number
  billedDate: string
  insPolId: number
  payerName: string | null
  lineCount: number
  totalAmount: number
}

const moneyFmt = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' })
const fmtDate = (iso: string) => {
  // Date-only string from server — render as calendar day, no TZ shift.
  const [y, m, d] = iso.split('-').map(Number)
  return new Date(y, m - 1, d).toLocaleDateString('en-US')
}

// Insurance Payment Tracer queue — pick a patient, see their billed-but-unpaid
// claim batches grouped by bill date, select one or more, and generate the
// AR follow-up PDF. Mirrors ChiroTouch's "Tracer" button on Claim History.
const TracerList = () => {
  const [patientInput, setPatientInput] = useState('')
  const [patientId, setPatientId] = useState<number | null>(null)
  const [batches, setBatches] = useState<Batch[] | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [selected, setSelected] = useState<Set<string>>(new Set())

  useEffect(() => {
    if (patientId == null) return
    setLoading(true)
    setError(null)
    setBatches(null)
    setSelected(new Set())
    fetch(`${WORKFLOW_API}/tracer/batches?patientId=${patientId}`)
      .then(async (r) => {
        if (!r.ok) throw new Error(`HTTP ${r.status}`)
        return r.json() as Promise<Batch[]>
      })
      .then(setBatches)
      .catch((e) => setError(e instanceof Error ? e.message : 'Failed to load batches.'))
      .finally(() => setLoading(false))
  }, [patientId])

  const totalSelected = useMemo(
    () =>
      (batches ?? [])
        .filter((b) => selected.has(b.id))
        .reduce((s, b) => s + b.totalAmount, 0),
    [batches, selected],
  )

  const toggleAll = () => {
    if (!batches) return
    setSelected((s) => (s.size === batches.length ? new Set() : new Set(batches.map((b) => b.id))))
  }

  const generate = () => {
    if (patientId == null || selected.size === 0) return
    const dates = Array.from(selected).join(',')
    window.open(
      withWorkflowToken(
        `${WORKFLOW_API}/tracer/preview?patientId=${patientId}&billDates=${dates}`,
      ),
      '_blank',
    )
  }

  return (
    <Box sx={{ mt: 1 }}>
      <Title title="Insurance Payment Tracer" />

      <Card sx={{ mb: 2 }}>
        <CardContent>
          <Typography variant="overline" color="text.secondary">
            Find patient
          </Typography>
          <Typography variant="body2" sx={{ mb: 1.5 }}>
            Enter a patient ID to see their billed-but-unpaid claim batches. Select one or more
            bill dates and generate the tracer PDF to send to the carrier.
          </Typography>
          <Stack direction="row" spacing={2} alignItems="center">
            <TextField
              size="small"
              label="Patient ID"
              value={patientInput}
              onChange={(e) => setPatientInput(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') {
                  const n = parseInt(patientInput, 10)
                  if (Number.isFinite(n) && n > 0) setPatientId(n)
                }
              }}
              sx={{ width: 180 }}
            />
            <Button
              variant="outlined"
              onClick={() => {
                const n = parseInt(patientInput, 10)
                if (Number.isFinite(n) && n > 0) setPatientId(n)
              }}
            >
              Load batches
            </Button>
          </Stack>
        </CardContent>
      </Card>

      {error && (
        <Alert severity="error" sx={{ mb: 2 }}>
          {error}
        </Alert>
      )}

      {loading && (
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mb: 2 }}>
          <CircularProgress size={18} />
          <Typography variant="body2">Loading batches…</Typography>
        </Box>
      )}

      {batches && batches.length === 0 && (
        <Alert severity="info">No outstanding billed-but-unpaid charges for patient {patientId}.</Alert>
      )}

      {batches && batches.length > 0 && (
        <Card>
          <CardContent>
            <Stack direction="row" spacing={2} alignItems="center" sx={{ mb: 1.5 }}>
              <Typography variant="h6" sx={{ flexGrow: 1 }}>
                {batches.length} batch{batches.length === 1 ? '' : 'es'} · Total{' '}
                {moneyFmt.format(batches.reduce((s, b) => s + b.totalAmount, 0))}
              </Typography>
              <Typography variant="body2" color="text.secondary">
                {selected.size} selected · {moneyFmt.format(totalSelected)}
              </Typography>
              <Button
                variant="contained"
                disabled={selected.size === 0}
                startIcon={<PictureAsPdf fontSize="small" />}
                onClick={generate}
              >
                Generate {selected.size > 1 ? 'tracers' : 'tracer'}
              </Button>
            </Stack>

            <Table size="small">
              <TableHead>
                <TableRow>
                  <TableCell padding="checkbox">
                    <Checkbox
                      indeterminate={selected.size > 0 && selected.size < batches.length}
                      checked={selected.size === batches.length}
                      onChange={toggleAll}
                    />
                  </TableCell>
                  <TableCell>Bill Date</TableCell>
                  <TableCell>Payer</TableCell>
                  <TableCell align="right">Lines</TableCell>
                  <TableCell align="right">Amount</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {batches.map((b) => (
                  <TableRow key={b.id} hover>
                    <TableCell padding="checkbox">
                      <Checkbox
                        checked={selected.has(b.id)}
                        onChange={() => {
                          setSelected((s) => {
                            const n = new Set(s)
                            if (n.has(b.id)) n.delete(b.id)
                            else n.add(b.id)
                            return n
                          })
                        }}
                      />
                    </TableCell>
                    <TableCell>{fmtDate(b.billedDate)}</TableCell>
                    <TableCell>{b.payerName ?? '—'}</TableCell>
                    <TableCell align="right">{b.lineCount}</TableCell>
                    <TableCell align="right">{moneyFmt.format(b.totalAmount)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </CardContent>
        </Card>
      )}
    </Box>
  )
}

export default TracerList
