import { useEffect, useMemo, useState } from 'react'
import { useNotify, useRecordContext } from 'react-admin'
import {
  Alert,
  Box,
  Button,
  Card,
  CardContent,
  Checkbox,
  CircularProgress,
  FormControlLabel,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TablePagination,
  TableRow,
  Typography,
} from '@mui/material'
import PictureAsPdf from '@mui/icons-material/PictureAsPdf'
import Send from '@mui/icons-material/Send'
import { withWorkflowToken } from '../../apiClient'

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
  const [y, m, d] = iso.split('-').map(Number)
  return new Date(y, m - 1, d).toLocaleDateString('en-US')
}

// Embedded on the case detail page. Lists the current patient's billed-but-
// unpaid claim batches (one row per bill date + payer) with checkboxes for
// multi-select, then generates the AR follow-up PDF for the selected batches.
// In-memory pagination because some patients have 50+ batches and a single
// long table is unreadable.
const TracerCard = () => {
  const record = useRecordContext()
  const patientId = (record?.patientId as number | undefined) ?? null
  const [batches, setBatches] = useState<Batch[] | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [page, setPage] = useState(0)
  const [rowsPerPage, setRowsPerPage] = useState(10)
  // Default on — the common workflow is "tracer + the HCFAs to back it up
  // in one fax". Uncheck to send the tracer alone.
  const [includeHcfa, setIncludeHcfa] = useState(true)
  const [faxing, setFaxing] = useState(false)
  const notify = useNotify()

  useEffect(() => {
    if (patientId == null) return
    setLoading(true)
    setError(null)
    setBatches(null)
    setSelected(new Set())
    setPage(0)
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

  const visibleBatches = useMemo(
    () => (batches ?? []).slice(page * rowsPerPage, (page + 1) * rowsPerPage),
    [batches, page, rowsPerPage],
  )

  // "Select all on this page" — only affects the visible rows. Common pattern
  // for paginated tables; full-list select-all gets confusing fast.
  const visibleAllSelected =
    visibleBatches.length > 0 && visibleBatches.every((b) => selected.has(b.id))
  const visibleSomeSelected = visibleBatches.some((b) => selected.has(b.id))

  const toggleVisibleAll = () => {
    setSelected((s) => {
      const n = new Set(s)
      if (visibleAllSelected) visibleBatches.forEach((b) => n.delete(b.id))
      else visibleBatches.forEach((b) => n.add(b.id))
      return n
    })
  }

  const generate = () => {
    if (patientId == null || selected.size === 0) return
    const dates = Array.from(selected).join(',')
    window.open(
      withWorkflowToken(
        `${WORKFLOW_API}/tracer/preview?patientId=${patientId}&billDates=${dates}&includeHcfa=${includeHcfa}`,
      ),
      '_blank',
    )
  }

  const faxNow = async () => {
    if (patientId == null || selected.size === 0) return
    const dates = Array.from(selected).join(',')
    setFaxing(true)
    try {
      const resp = await fetch(
        `${WORKFLOW_API}/fax/tracer?patientId=${patientId}&billDates=${dates}&includeHcfa=${includeHcfa}`,
        { method: 'POST' },
      )
      const body = await resp.json().catch(() => ({}))
      if (!resp.ok) throw new Error(body?.error || `HTTP ${resp.status}`)
      const sent = body?.sent ?? 0
      const ids = (body?.results ?? [])
        .map((r: { faxId?: string; to?: string }) => `${r.to ?? '?'}→${r.faxId ?? '?'}`)
        .join(', ')
      notify(`Fax sent (${sent}): ${ids}`, { type: 'success' })
    } catch (e) {
      notify(`Fax failed: ${e instanceof Error ? e.message : 'unknown error'}`, {
        type: 'error',
      })
    } finally {
      setFaxing(false)
    }
  }

  if (patientId == null) return null

  return (
    <Card>
      <CardContent>
        <Stack direction="row" alignItems="center" spacing={2} sx={{ mb: 1 }}>
          <Typography variant="h6" sx={{ flexGrow: 1 }}>
            Insurance Payment Tracer
          </Typography>
          {batches && batches.length > 0 && (
            <>
              <FormControlLabel
                control={
                  <Checkbox
                    size="small"
                    checked={includeHcfa}
                    onChange={(e) => setIncludeHcfa(e.target.checked)}
                  />
                }
                label="Include HCFA"
                sx={{ mr: 0 }}
              />
              <Typography variant="body2" color="text.secondary">
                {selected.size} selected · {moneyFmt.format(totalSelected)}
              </Typography>
              <Button
                size="small"
                variant="outlined"
                disabled={selected.size === 0}
                startIcon={<PictureAsPdf fontSize="small" />}
                onClick={generate}
              >
                Generate {selected.size > 1 ? 'tracers' : 'tracer'}
              </Button>
              <Button
                size="small"
                variant="contained"
                color="primary"
                disabled={selected.size === 0 || faxing}
                startIcon={
                  faxing ? <CircularProgress size={14} /> : <Send fontSize="small" />
                }
                onClick={faxNow}
              >
                {faxing ? 'Faxing…' : 'Fax now'}
              </Button>
            </>
          )}
        </Stack>

        <Typography variant="body2" color="text.secondary" sx={{ mb: 1.5 }}>
          Billed-but-unpaid claim batches grouped by bill date. Select one or more rows and
          generate the AR follow-up PDF to send to the carrier.
        </Typography>

        {error && (
          <Alert severity="error" sx={{ mb: 1 }}>
            {error}
          </Alert>
        )}

        {loading && (
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mb: 1 }}>
            <CircularProgress size={16} />
            <Typography variant="body2">Loading batches…</Typography>
          </Box>
        )}

        {batches && batches.length === 0 && (
          <Typography variant="body2" color="text.secondary">
            No outstanding billed-but-unpaid charges.
          </Typography>
        )}

        {batches && batches.length > 0 && (
          <>
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 1 }}>
              {batches.length} total batch{batches.length === 1 ? '' : 'es'} ·{' '}
              {moneyFmt.format(batches.reduce((s, b) => s + b.totalAmount, 0))} total
            </Typography>
            <Table size="small">
              <TableHead>
                <TableRow>
                  <TableCell padding="checkbox">
                    <Checkbox
                      indeterminate={visibleSomeSelected && !visibleAllSelected}
                      checked={visibleAllSelected}
                      onChange={toggleVisibleAll}
                    />
                  </TableCell>
                  <TableCell>Bill Date</TableCell>
                  <TableCell>Payer</TableCell>
                  <TableCell align="right">Lines</TableCell>
                  <TableCell align="right">Amount</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {visibleBatches.map((b) => (
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
            <TablePagination
              component="div"
              count={batches.length}
              page={page}
              onPageChange={(_, p) => setPage(p)}
              rowsPerPage={rowsPerPage}
              rowsPerPageOptions={[10, 25, 50]}
              onRowsPerPageChange={(e) => {
                setRowsPerPage(parseInt(e.target.value, 10))
                setPage(0)
              }}
            />
          </>
        )}
      </CardContent>
    </Card>
  )
}

export default TracerCard
