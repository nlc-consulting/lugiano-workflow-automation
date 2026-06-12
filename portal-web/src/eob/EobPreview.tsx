import { useState } from 'react'
import { Title } from 'react-admin'
import {
  Alert,
  Box,
  Button,
  Card,
  CardContent,
  Chip,
  CircularProgress,
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
import CheckCircle from '@mui/icons-material/CheckCircle'
import HelpOutline from '@mui/icons-material/HelpOutline'
import ErrorOutline from '@mui/icons-material/ErrorOutline'

const WORKFLOW_API = import.meta.env.VITE_WORKFLOW_API_URL || '/workflow-api'

type EobLine = {
  patientName: string
  dateOfService: string | null
  proceduralCode: string
  individualCharge: number
  writeOffAmount: number
  paidAmount: number
  allowedCharge: number
  reasonCode: string
  reasonDescription: string
  claimNumber: string
  billNumber: string
  checkNumber: string
}

type TxCandidate = {
  tranId: number
  patID: number
  tranDate: string
  code: string | null
  tranAmt: number
  priPaidAmt: number
  woAmt: number
}

type ProposedUpdate = {
  tranId: number
  currentPriPaidAmt: number
  proposedPriPaidAmt: number
  currentWOAmt: number
  proposedWOAmt: number
  reasonCode: string
  reasonDescription: string
}

type PreviewResult = {
  totalLines: number
  matched: { line: EobLine; match: TxCandidate; proposed: ProposedUpdate }[]
  ambiguous: { line: EobLine; candidates: TxCandidate[]; reason: string }[]
  unmatched: { line: EobLine; reason: string }[]
}

const money = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' })
const fmtDate = (iso: string | null) =>
  iso ? new Date(iso).toLocaleDateString('en-US', { timeZone: 'America/New_York' }) : '—'

const EobPreview = () => {
  const [file, setFile] = useState<File | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const [result, setResult] = useState<PreviewResult | null>(null)
  const [error, setError] = useState<string | null>(null)

  const submit = async () => {
    if (!file) return
    setSubmitting(true)
    setError(null)
    setResult(null)
    const form = new FormData()
    form.append('file', file)
    try {
      const res = await fetch(`${WORKFLOW_API}/eob/preview`, { method: 'POST', body: form })
      if (!res.ok) {
        const body = await res.json().catch(() => ({}))
        throw new Error(body.error ?? `HTTP ${res.status}`)
      }
      setResult((await res.json()) as PreviewResult)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Preview failed.')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Box sx={{ mt: 1 }}>
      <Title title="EOB Posting Preview" />

      <Card sx={{ mb: 2 }}>
        <CardContent>
          <Typography variant="overline" color="text.secondary">
            Upload EOB workbook
          </Typography>
          <Typography variant="body2" sx={{ mb: 1.5 }}>
            Select an EOB Line Items <code>.xlsx</code> (the same format as the Lugiano export).
            We'll parse it, match each line to a PSChiro charge, and show the proposed posting.
            Nothing writes back — preview only.
          </Typography>
          <Stack direction="row" spacing={2} alignItems="center">
            <Button variant="outlined" component="label" startIcon={<UploadFile />}>
              {file ? file.name : 'Choose .xlsx'}
              <input
                hidden
                type="file"
                accept=".xlsx"
                onChange={(e) => setFile(e.target.files?.[0] ?? null)}
              />
            </Button>
            <Button
              variant="contained"
              onClick={submit}
              disabled={!file || submitting}
              startIcon={submitting ? <CircularProgress size={16} /> : null}
            >
              {submitting ? 'Matching…' : 'Preview posting'}
            </Button>
          </Stack>
          {error && (
            <Alert severity="error" sx={{ mt: 2 }}>
              {error}
            </Alert>
          )}
        </CardContent>
      </Card>

      {result && (
        <>
          <Stack direction="row" spacing={1} sx={{ mb: 2 }}>
            <Chip
              icon={<CheckCircle />}
              color="success"
              label={`${result.matched.length} matched`}
              sx={{ fontWeight: 600 }}
            />
            <Chip
              icon={<HelpOutline />}
              color="warning"
              label={`${result.ambiguous.length} ambiguous`}
              sx={{ fontWeight: 600 }}
            />
            <Chip
              icon={<ErrorOutline />}
              color="error"
              label={`${result.unmatched.length} unmatched`}
              sx={{ fontWeight: 600 }}
            />
            <Chip variant="outlined" label={`${result.totalLines} total lines`} />
          </Stack>

          {result.matched.length > 0 && (
            <Card sx={{ mb: 2 }}>
              <CardContent>
                <Typography variant="h6" sx={{ mb: 1 }}>
                  Matched — proposed updates ({result.matched.length})
                </Typography>
                <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 1 }}>
                  Sorted by net update size — biggest dollar amount to post at the top,
                  $0/$0 lines (denials, policy exhaustion) at the bottom.
                </Typography>
                <TableContainer>
                  <Table size="small">
                    <TableHead>
                      <TableRow>
                        <TableCell>Patient</TableCell>
                        <TableCell>DOS</TableCell>
                        <TableCell>CPT</TableCell>
                        <TableCell align="right">Charge</TableCell>
                        <TableCell align="right">PriPaid Δ</TableCell>
                        <TableCell align="right">WriteOff Δ</TableCell>
                        <TableCell>Reason</TableCell>
                        <TableCell align="right">TranId</TableCell>
                      </TableRow>
                    </TableHead>
                    <TableBody>
                      {[...result.matched]
                        .sort((a, b) => {
                          // Biggest meaningful write wins. Sum of the two
                          // deltas as the score — pure-zero rows (denials)
                          // naturally sink to the bottom.
                          const aDelta =
                            Math.abs(a.proposed.proposedPriPaidAmt - a.proposed.currentPriPaidAmt) +
                            Math.abs(a.proposed.proposedWOAmt - a.proposed.currentWOAmt)
                          const bDelta =
                            Math.abs(b.proposed.proposedPriPaidAmt - b.proposed.currentPriPaidAmt) +
                            Math.abs(b.proposed.proposedWOAmt - b.proposed.currentWOAmt)
                          return bDelta - aDelta
                        })
                        .map((m, i) => (
                        <TableRow key={i}>
                          <TableCell>{m.line.patientName}</TableCell>
                          <TableCell>{fmtDate(m.line.dateOfService)}</TableCell>
                          <TableCell>{m.line.proceduralCode}</TableCell>
                          <TableCell align="right">{money.format(m.line.individualCharge)}</TableCell>
                          <TableCell align="right">
                            {money.format(m.proposed.currentPriPaidAmt)} →{' '}
                            <strong>{money.format(m.proposed.proposedPriPaidAmt)}</strong>
                          </TableCell>
                          <TableCell align="right">
                            {money.format(m.proposed.currentWOAmt)} →{' '}
                            <strong>{money.format(m.proposed.proposedWOAmt)}</strong>
                          </TableCell>
                          <TableCell>
                            <Typography variant="caption">
                              {m.proposed.reasonCode} · {m.proposed.reasonDescription}
                            </Typography>
                          </TableCell>
                          <TableCell align="right">
                            <Typography variant="caption" color="text.secondary">
                              {m.match.tranId}
                            </Typography>
                          </TableCell>
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                </TableContainer>
              </CardContent>
            </Card>
          )}

          {result.ambiguous.length > 0 && (
            <Card sx={{ mb: 2 }}>
              <CardContent>
                <Typography variant="h6" sx={{ mb: 1 }}>
                  Ambiguous — needs operator review ({result.ambiguous.length})
                </Typography>
                <TableContainer>
                  <Table size="small">
                    <TableHead>
                      <TableRow>
                        <TableCell>Patient</TableCell>
                        <TableCell>DOS</TableCell>
                        <TableCell>CPT</TableCell>
                        <TableCell align="right">EOB Charge</TableCell>
                        <TableCell>Candidates</TableCell>
                        <TableCell>Reason</TableCell>
                      </TableRow>
                    </TableHead>
                    <TableBody>
                      {result.ambiguous.map((a, i) => (
                        <TableRow key={i}>
                          <TableCell>{a.line.patientName}</TableCell>
                          <TableCell>{fmtDate(a.line.dateOfService)}</TableCell>
                          <TableCell>{a.line.proceduralCode}</TableCell>
                          <TableCell align="right">{money.format(a.line.individualCharge)}</TableCell>
                          <TableCell>
                            {a.candidates.map((c) => (
                              <Typography key={c.tranId} variant="caption" sx={{ display: 'block' }}>
                                #{c.tranId} · {money.format(c.tranAmt)}
                              </Typography>
                            ))}
                          </TableCell>
                          <TableCell>
                            <Typography variant="caption">{a.reason}</Typography>
                          </TableCell>
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                </TableContainer>
              </CardContent>
            </Card>
          )}

          {result.unmatched.length > 0 && (
            <Card sx={{ mb: 2 }}>
              <CardContent>
                <Typography variant="h6" sx={{ mb: 1 }}>
                  Unmatched ({result.unmatched.length})
                </Typography>
                <TableContainer>
                  <Table size="small">
                    <TableHead>
                      <TableRow>
                        <TableCell>Patient</TableCell>
                        <TableCell>DOS</TableCell>
                        <TableCell>CPT</TableCell>
                        <TableCell align="right">Charge</TableCell>
                        <TableCell>Reason</TableCell>
                      </TableRow>
                    </TableHead>
                    <TableBody>
                      {result.unmatched.map((u, i) => (
                        <TableRow key={i}>
                          <TableCell>{u.line.patientName}</TableCell>
                          <TableCell>{fmtDate(u.line.dateOfService)}</TableCell>
                          <TableCell>{u.line.proceduralCode}</TableCell>
                          <TableCell align="right">{money.format(u.line.individualCharge)}</TableCell>
                          <TableCell>
                            <Typography variant="caption">{u.reason}</Typography>
                          </TableCell>
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                </TableContainer>
              </CardContent>
            </Card>
          )}

          <Alert severity="info" sx={{ mb: 2 }}>
            Preview only — no writes have been applied to ChiroTouch. Apply path comes in a
            follow-up that requires broader write permissions.
          </Alert>
        </>
      )}
    </Box>
  )
}

export default EobPreview
