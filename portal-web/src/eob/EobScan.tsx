import { useEffect, useRef, useState } from 'react'
import { Title } from 'react-admin'
import {
  Alert,
  Box,
  Button,
  Card,
  CardContent,
  Chip,
  CircularProgress,
  LinearProgress,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  Typography,
} from '@mui/material'
import UploadFile from '@mui/icons-material/UploadFile'
import Download from '@mui/icons-material/Download'
import CheckCircle from '@mui/icons-material/CheckCircle'
import ErrorOutline from '@mui/icons-material/ErrorOutline'

const WORKFLOW_API = import.meta.env.VITE_WORKFLOW_API_URL || '/workflow-api'

// One row per check stub found on the scanned mail (front of an EOB envelope).
type ScanCheck = {
  id: number
  pageNumber: number
  checkNumber: string
  checkDate: string | null
  amount: number
  payer: string | null
  administrator: string | null
}

// One row per CPT/HCPCS line item on an EOB. Reason codes carry through as an
// array of {code, description} — the "why the carrier paid what they paid".
type ScanLineItem = {
  id: number
  pageNumber: number
  claimNumber: string | null
  patientNameRaw: string | null
  billNumber: string | null
  serviceDate: string | null
  checkNumber: string | null
  procedureCode: string
  billedAmount: number
  allowedAmount: number
  paidAmount: number
  writeOffAmount: number
  reasonCodes: { code: string; description: string | null }[]
}

type ScanStatus = {
  id: number
  status: 'queued' | 'running' | 'completed' | 'failed'
  errorMessage: string | null
  sourceFilename: string
  scanDate: string | null
  pageCount: number
  fileSizeBytes: number
  uploadedAt: string
  processingStartedAt: string | null
  completedAt: string | null
  chunkSize: number
  chunkOverlap: number
  modelUsed: string
  inputTokens: number
  outputTokens: number
  estimatedCostUsd: number
  checks: ScanCheck[]
  lineItems: ScanLineItem[]
}

const moneyFmt = new Intl.NumberFormat('en-US', {
  style: 'currency',
  currency: 'USD',
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
})

const EobScan = () => {
  const [file, setFile] = useState<File | null>(null)
  const [uploading, setUploading] = useState(false)
  const [uploadError, setUploadError] = useState<string | null>(null)
  const [scan, setScan] = useState<ScanStatus | null>(null)
  const pollRef = useRef<number | null>(null)

  // Poll while queued/running. Interval intentionally coarse (5s) because
  // extraction takes minutes; a snappier poll would just hammer the API for
  // no benefit.
  useEffect(() => {
    if (!scan) return
    if (scan.status === 'completed' || scan.status === 'failed') {
      if (pollRef.current) {
        window.clearInterval(pollRef.current)
        pollRef.current = null
      }
      return
    }
    if (pollRef.current) return  // already polling
    pollRef.current = window.setInterval(async () => {
      try {
        const resp = await fetch(`${WORKFLOW_API}/eob/scan/${scan.id}`)
        if (resp.ok) {
          const next = (await resp.json()) as ScanStatus
          setScan(next)
        }
      } catch {
        // Transient network errors — next tick will retry.
      }
    }, 5000)
    return () => {
      if (pollRef.current) {
        window.clearInterval(pollRef.current)
        pollRef.current = null
      }
    }
  }, [scan?.status, scan?.id])

  const handleUpload = async () => {
    if (!file) return
    setUploading(true)
    setUploadError(null)
    try {
      const form = new FormData()
      form.append('file', file)
      const resp = await fetch(`${WORKFLOW_API}/eob/scan`, {
        method: 'POST',
        body: form,
      })
      if (!resp.ok) {
        const text = await resp.text()
        throw new Error(`HTTP ${resp.status}: ${text || 'upload failed'}`)
      }
      const initial = (await resp.json()) as { id: number }
      // Fetch full status right away so the polling effect kicks in.
      const statusResp = await fetch(`${WORKFLOW_API}/eob/scan/${initial.id}`)
      const status = (await statusResp.json()) as ScanStatus
      setScan(status)
      setFile(null)
    } catch (e) {
      setUploadError(e instanceof Error ? e.message : String(e))
    } finally {
      setUploading(false)
    }
  }

  const handleDownloadXlsx = async () => {
    if (!scan) return
    // Fetch + blob (not window.open) so the workflowAuth monkeypatch attaches
    // the JWT bearer. window.open() would 401 — browser navigations don't
    // pick up custom Authorization headers.
    const resp = await fetch(`${WORKFLOW_API}/eob/scan/${scan.id}/export.xlsx`)
    if (!resp.ok) return
    const blob = await resp.blob()
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `EOB_Scan_${scan.id}_${scan.sourceFilename.replace(/\.pdf$/i, '')}.xlsx`
    document.body.appendChild(a)
    a.click()
    a.remove()
    URL.revokeObjectURL(url)
  }

  const handleReset = () => {
    setScan(null)
    setFile(null)
    setUploadError(null)
  }

  return (
    <Box sx={{ p: 3 }}>
      <Title title="EOB Scan (Mail-Scan Parser)" />
      <Typography variant="h5" gutterBottom>
        EOB Scan
      </Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 3 }}>
        Upload the day's scanned mail PDF. The parser will extract every check
        stub and every line item, then produce an EOB Details spreadsheet
        matching the current vendor's output — but in a fraction of the time
        and without the manual xlsx round-trip.
      </Typography>

      {!scan && (
        <Card sx={{ maxWidth: 640 }}>
          <CardContent>
            <Stack spacing={2}>
              <Button
                component="label"
                variant="outlined"
                startIcon={<UploadFile />}
                sx={{ alignSelf: 'flex-start' }}
              >
                Choose PDF
                <input
                  type="file"
                  accept="application/pdf,.pdf"
                  hidden
                  onChange={(e) => setFile(e.target.files?.[0] ?? null)}
                />
              </Button>
              {file && (
                <Typography variant="body2">
                  Selected: <strong>{file.name}</strong>{' '}
                  <Typography component="span" color="text.secondary">
                    ({(file.size / 1024 / 1024).toFixed(1)} MB)
                  </Typography>
                </Typography>
              )}
              <Button
                variant="contained"
                onClick={handleUpload}
                disabled={!file || uploading}
                startIcon={uploading ? <CircularProgress size={16} /> : undefined}
                sx={{ alignSelf: 'flex-start' }}
              >
                {uploading ? 'Uploading…' : 'Start scan'}
              </Button>
              {uploadError && <Alert severity="error">{uploadError}</Alert>}
            </Stack>
          </CardContent>
        </Card>
      )}

      {scan && (
        <>
          <ScanHeader scan={scan} onReset={handleReset} onDownload={handleDownloadXlsx} />
          {scan.status === 'running' || scan.status === 'queued' ? (
            <ProcessingCard scan={scan} />
          ) : scan.status === 'failed' ? (
            <Alert severity="error" sx={{ mt: 2 }}>
              Scan failed: {scan.errorMessage || 'unknown error'}
            </Alert>
          ) : (
            <>
              <SummaryChips scan={scan} />
              <ChecksTable checks={scan.checks} />
              <LineItemsTable items={scan.lineItems} />
            </>
          )}
        </>
      )}
    </Box>
  )
}

const ScanHeader = ({
  scan,
  onReset,
  onDownload,
}: {
  scan: ScanStatus
  onReset: () => void
  onDownload: () => void
}) => (
  <Stack direction="row" spacing={2} alignItems="center" sx={{ mt: 1, mb: 2 }}>
    <Typography variant="subtitle1" sx={{ fontWeight: 600 }}>
      {scan.sourceFilename}
    </Typography>
    <Chip
      size="small"
      color={
        scan.status === 'completed'
          ? 'success'
          : scan.status === 'failed'
          ? 'error'
          : 'primary'
      }
      icon={
        scan.status === 'completed' ? (
          <CheckCircle />
        ) : scan.status === 'failed' ? (
          <ErrorOutline />
        ) : undefined
      }
      label={scan.status}
    />
    <Typography variant="body2" color="text.secondary">
      {scan.pageCount} pages
    </Typography>
    <Box sx={{ flex: 1 }} />
    {scan.status === 'completed' && (
      <Button variant="outlined" startIcon={<Download />} onClick={onDownload}>
        Download xlsx
      </Button>
    )}
    <Button variant="text" onClick={onReset}>
      Upload another
    </Button>
  </Stack>
)

const ProcessingCard = ({ scan }: { scan: ScanStatus }) => {
  const elapsedMin = scan.processingStartedAt
    ? (Date.now() - new Date(scan.processingStartedAt).getTime()) / 60000
    : 0
  // Very rough estimate: 15-page chunks, 4-way parallel, ~60s per chunk.
  const estTotalMin = Math.max(2, (scan.pageCount / 15 / 4) * 1)
  const progress = Math.min(95, (elapsedMin / estTotalMin) * 100)
  return (
    <Card sx={{ mt: 2 }}>
      <CardContent>
        <Stack spacing={2}>
          <Typography>Extracting… (roughly {estTotalMin.toFixed(0)} minutes total)</Typography>
          <LinearProgress variant="determinate" value={progress} />
          <Typography variant="body2" color="text.secondary">
            {elapsedMin.toFixed(1)} min elapsed. Status refreshes every 5 seconds.
          </Typography>
        </Stack>
      </CardContent>
    </Card>
  )
}

const SummaryChips = ({ scan }: { scan: ScanStatus }) => (
  <Stack direction="row" spacing={1} sx={{ mb: 2 }}>
    <Chip label={`${scan.checks.length} checks`} color="primary" variant="outlined" />
    <Chip label={`${scan.lineItems.length} line items`} color="primary" variant="outlined" />
    <Chip
      label={`${moneyFmt.format(scan.estimatedCostUsd)} Claude cost`}
      variant="outlined"
    />
    <Chip
      label={`${scan.inputTokens.toLocaleString()} in + ${scan.outputTokens.toLocaleString()} out tokens`}
      variant="outlined"
    />
  </Stack>
)

const ChecksTable = ({ checks }: { checks: ScanCheck[] }) => (
  <Card sx={{ mb: 2 }}>
    <CardContent>
      <Typography variant="h6" gutterBottom>
        Checks
      </Typography>
      <TableContainer>
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell>Page</TableCell>
              <TableCell>Check #</TableCell>
              <TableCell>Date</TableCell>
              <TableCell align="right">Amount</TableCell>
              <TableCell>Payer</TableCell>
              <TableCell>Administrator</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {checks.map((c) => (
              <TableRow key={c.id}>
                <TableCell>{c.pageNumber}</TableCell>
                <TableCell sx={{ fontFamily: 'monospace' }}>{c.checkNumber}</TableCell>
                <TableCell>{c.checkDate}</TableCell>
                <TableCell align="right">{moneyFmt.format(c.amount)}</TableCell>
                <TableCell>{c.payer}</TableCell>
                <TableCell>{c.administrator}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </TableContainer>
    </CardContent>
  </Card>
)

const LineItemsTable = ({ items }: { items: ScanLineItem[] }) => (
  <Card>
    <CardContent>
      <Typography variant="h6" gutterBottom>
        Line items
      </Typography>
      <TableContainer sx={{ maxHeight: 600 }}>
        <Table size="small" stickyHeader>
          <TableHead>
            <TableRow>
              <TableCell>Page</TableCell>
              <TableCell>Patient</TableCell>
              <TableCell>DOS</TableCell>
              <TableCell>Claim #</TableCell>
              <TableCell>CPT</TableCell>
              <TableCell align="right">Billed</TableCell>
              <TableCell align="right">Allowed</TableCell>
              <TableCell align="right">Paid</TableCell>
              <TableCell align="right">Write-off</TableCell>
              <TableCell>Reason codes</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {items.map((l) => (
              <TableRow key={l.id}>
                <TableCell>{l.pageNumber}</TableCell>
                <TableCell>{l.patientNameRaw}</TableCell>
                <TableCell>{l.serviceDate}</TableCell>
                <TableCell sx={{ fontFamily: 'monospace', fontSize: 12 }}>
                  {l.claimNumber}
                </TableCell>
                <TableCell sx={{ fontFamily: 'monospace' }}>{l.procedureCode}</TableCell>
                <TableCell align="right">{moneyFmt.format(l.billedAmount)}</TableCell>
                <TableCell align="right">{moneyFmt.format(l.allowedAmount)}</TableCell>
                <TableCell align="right">{moneyFmt.format(l.paidAmount)}</TableCell>
                <TableCell align="right">{moneyFmt.format(l.writeOffAmount)}</TableCell>
                <TableCell sx={{ fontSize: 12 }}>
                  {l.reasonCodes.map((r) => r.code).join(' / ')}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </TableContainer>
    </CardContent>
  </Card>
)

export default EobScan
