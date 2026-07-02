import { useMemo, useState } from 'react'
import { Title } from 'react-admin'
import {
  Accordion,
  AccordionDetails,
  AccordionSummary,
  Alert,
  Box,
  Button,
  Card,
  CardContent,
  Chip,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
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
import Send from '@mui/icons-material/Send'
import ExpandMore from '@mui/icons-material/ExpandMore'
import Person from '@mui/icons-material/Person'
import LinkIcon from '@mui/icons-material/Link'
import TextField from '@mui/material/TextField'

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

type PatientSuggestion = { patientId: number; fullName: string; score: number }

type PreviewResult = {
  totalLines: number
  matched: { line: EobLine; match: TxCandidate; proposed: ProposedUpdate }[]
  ambiguous: { line: EobLine; candidates: TxCandidate[]; reason: string }[]
  unmatched: { line: EobLine; reason: string; suggestions?: PatientSuggestion[] }[]
}

type ResolveResult = {
  matched: { line: EobLine; match: TxCandidate; proposed: ProposedUpdate } | null
  ambiguous: { line: EobLine; candidates: TxCandidate[]; reason: string } | null
  unmatched: { line: EobLine; reason: string; suggestions?: PatientSuggestion[] } | null
}

type AppliedLine = {
  line: EobLine
  tranId: number
  billedChargeId: number | null
  priPaidAmtBefore: number
  priPaidAmtAfter: number
  woAmtBefore: number
  woAmtAfter: number
  billedChargeStamped: boolean
}
type SkippedLine = { line: EobLine; tranId: number; reason: string }
type ApplyResult = {
  totalLines: number
  applied: AppliedLine[]
  skipped: SkippedLine[]
  ambiguous: { line: EobLine; reason: string }[]
  unmatched: { line: EobLine; reason: string }[]
}

const money = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' })
const fmtDate = (iso: string | null) =>
  iso ? new Date(iso).toLocaleDateString('en-US', { timeZone: 'America/New_York' }) : '—'

// Group EOB lines by patient name so the operator can collapse to a per-
// patient summary first, then drill into individual lines. Preserves the
// caller's preferred row ordering inside each group.
function groupBy<T>(items: T[], keyFn: (t: T) => string): { key: string; rows: T[] }[] {
  const map = new Map<string, T[]>()
  const displayName = new Map<string, string>()
  for (const item of items) {
    const raw = keyFn(item) || '(no patient)'
    // Canonical key: strip punctuation, uppercase, sort tokens — so
    // "AMADO ZAMBRANO" and "ZAMBRANO, AMADO" land in the same group.
    const norm = raw
      .replace(/[^A-Za-z0-9 ]/g, ' ')
      .toUpperCase()
      .split(/\s+/)
      .filter(Boolean)
      .sort()
      .join(' ')
    const list = map.get(norm) ?? []
    list.push(item)
    map.set(norm, list)
    if (!displayName.has(norm)) displayName.set(norm, raw.trim())
  }
  return Array.from(map.entries()).map(([norm, rows]) => ({
    key: displayName.get(norm) ?? norm,
    rows,
  }))
}

const EobPreview = () => {
  const [file, setFile] = useState<File | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const [result, setResult] = useState<PreviewResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [confirmOpen, setConfirmOpen] = useState(false)
  const [applying, setApplying] = useState(false)
  const [applyResult, setApplyResult] = useState<ApplyResult | null>(null)
  // Manual "Map to CT #" override — opens a dialog scoped to a specific
  // patient group. Tracks the group rows being mapped + the lookup state.
  const [mapTarget, setMapTarget] = useState<{ patient: string; rows: { line: EobLine }[] } | null>(null)
  const [mapAcctInput, setMapAcctInput] = useState('')
  const [mapLookup, setMapLookup] = useState<{
    patientId: number
    accountNo: number | null
    fullName: string | null
    birthDate: string | null
  } | null>(null)
  const [mapLookupError, setMapLookupError] = useState<string | null>(null)
  const [mapLookupBusy, setMapLookupBusy] = useState(false)

  const submit = async () => {
    if (!file) return
    setSubmitting(true)
    setError(null)
    setResult(null)
    setApplyResult(null)
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

  // Operator clicked a fuzzy patient suggestion at the group header.
  // We re-resolve EVERY unmatched line in that group against the chosen
  // patientId in one shot — usually all lines for the same EOB patient
  // share the right PSChiro patient, so resolving line-by-line is busywork.
  // Calls run in parallel; state updates once at the end.
  const resolvePatientGroup = async (
    lines: { line: EobLine }[],
    patientId: number,
  ) => {
    if (!result || lines.length === 0) return
    try {
      const results = await Promise.all(
        lines.map((u) =>
          fetch(`${WORKFLOW_API}/eob/resolve-line`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ line: u.line, patientId }),
          }).then((r) => r.json() as Promise<ResolveResult>),
        ),
      )
      // Splice the resolved lines out of unmatched (by identity), then
      // append each result into its new bucket.
      const toRemove = new Set(lines.map((u) => u.line))
      setResult((prev) => {
        if (!prev) return prev
        const newUnmatched = prev.unmatched.filter((u) => !toRemove.has(u.line))
        const addMatched = results.map((r) => r.matched).filter(Boolean) as PreviewResult['matched']
        const addAmbig = results.map((r) => r.ambiguous).filter(Boolean) as PreviewResult['ambiguous']
        const addUnmatched = results.map((r) => r.unmatched).filter(Boolean) as PreviewResult['unmatched']
        return {
          ...prev,
          matched: [...prev.matched, ...addMatched],
          ambiguous: [...prev.ambiguous, ...addAmbig],
          unmatched: [...newUnmatched, ...addUnmatched],
        }
      })
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Resolve failed.')
    }
  }

  // Split matched rows into "clean apply" vs "would skip on apply" so the
  // headline count + display reflect what would genuinely post. Apply's
  // guards in EobPostingService skip these anyway:
  //   - proposed paid+WO > charge        → over-payment skip
  //   - existing paid+WO already = charge → already-posted skip
  // We don't have BilledCharges.PaidDate on the preview payload, so we
  // infer "already posted" from the totals.
  const { cleanMatched, skipMatched } = useMemo(() => {
    if (!result) return { cleanMatched: [], skipMatched: [] }
    const TOL = 0.01
    const isSkip = (m: PreviewResult['matched'][number]) => {
      const cur = m.proposed.currentPriPaidAmt + m.proposed.currentWOAmt
      const next = m.proposed.proposedPriPaidAmt + m.proposed.proposedWOAmt
      return next > m.match.tranAmt + TOL || cur >= m.match.tranAmt - TOL
    }
    return {
      cleanMatched: result.matched.filter((m) => !isSkip(m)),
      skipMatched: result.matched.filter(isSkip),
    }
  }, [result])

  const openMapDialog = (patient: string, rows: { line: EobLine }[]) => {
    setMapTarget({ patient, rows })
    setMapAcctInput('')
    setMapLookup(null)
    setMapLookupError(null)
  }

  const closeMapDialog = () => setMapTarget(null)

  const lookupAccountNo = async () => {
    const n = parseInt(mapAcctInput, 10)
    if (!Number.isFinite(n) || n <= 0) {
      setMapLookupError('Enter a valid account number.')
      return
    }
    setMapLookupBusy(true)
    setMapLookupError(null)
    setMapLookup(null)
    try {
      const res = await fetch(`${WORKFLOW_API}/eob/lookup-patient?accountNo=${n}`)
      const body = await res.json().catch(() => ({}))
      if (!res.ok) {
        setMapLookupError(body?.error || `No patient with account # ${n}.`)
        return
      }
      setMapLookup(body)
    } catch (e) {
      setMapLookupError(e instanceof Error ? e.message : 'Lookup failed.')
    } finally {
      setMapLookupBusy(false)
    }
  }

  const confirmMap = async () => {
    if (!mapTarget || !mapLookup) return
    const rows = mapTarget.rows
    closeMapDialog()
    await resolvePatientGroup(rows, mapLookup.patientId)
  }

  const apply = async () => {
    if (!file) return
    setConfirmOpen(false)
    setApplying(true)
    setError(null)
    const form = new FormData()
    form.append('file', file)
    try {
      const res = await fetch(`${WORKFLOW_API}/eob/apply`, { method: 'POST', body: form })
      const body = await res.json().catch(() => ({}))
      if (!res.ok) throw new Error(body.error ?? `HTTP ${res.status}`)
      setApplyResult(body as ApplyResult)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Apply failed.')
    } finally {
      setApplying(false)
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
            Use <strong>Apply</strong> to write the matched lines back to PSChiro.
          </Typography>
          <Stack direction="row" spacing={2} alignItems="center" flexWrap="wrap">
            <Button variant="outlined" component="label" startIcon={<UploadFile />}>
              {file ? file.name : 'Choose .xlsx'}
              <input
                hidden
                type="file"
                accept=".xlsx"
                onChange={(e) => {
                  setFile(e.target.files?.[0] ?? null)
                  setResult(null)
                  setApplyResult(null)
                }}
              />
            </Button>
            <Button
              variant="outlined"
              onClick={submit}
              disabled={!file || submitting || applying}
              startIcon={submitting ? <CircularProgress size={16} /> : null}
            >
              {submitting ? 'Matching…' : 'Preview posting'}
            </Button>
            {/* Apply path is disabled — current write logic only updates
                charge totals + BilledCharges. CT's real EOB posting also
                creates PaymentClaims envelope + per-line payment Transactions
                row (TranType='P'). Re-enable after the full hierarchy lands. */}
            <Button
              variant="contained"
              color="primary"
              disabled
              startIcon={<Send fontSize="small" />}
            >
              Apply (disabled — rebuild in progress)
            </Button>
          </Stack>
          {error && (
            <Alert severity="error" sx={{ mt: 2 }}>
              {error}
            </Alert>
          )}
          {applyResult && (
            <Alert severity="success" sx={{ mt: 2 }}>
              Posted to PSChiro: <strong>{applyResult.applied.length}</strong> applied,{' '}
              <strong>{applyResult.skipped.length}</strong> skipped (already posted /
              over-payment guard), <strong>{applyResult.ambiguous.length}</strong>{' '}
              ambiguous, <strong>{applyResult.unmatched.length}</strong> unmatched.
            </Alert>
          )}
        </CardContent>
      </Card>

      {/* Manual patient mapping by ChiroTouch AccountNo. Operator types the
          number, we look it up, show name + DOB for confirmation, and on
          confirm fan out the resolve across every row in the unmatched
          patient group. */}
      <Dialog open={mapTarget !== null} onClose={closeMapDialog} maxWidth="xs" fullWidth>
        <DialogTitle>Map to ChiroTouch account</DialogTitle>
        <DialogContent>
          <DialogContentText sx={{ mb: 2 }}>
            EOB shows <strong>{mapTarget?.patient}</strong> ({mapTarget?.rows.length} line
            {mapTarget?.rows.length === 1 ? '' : 's'}). Enter the ChiroTouch account number
            and confirm the patient before applying.
          </DialogContentText>
          <Stack direction="row" spacing={1}>
            <TextField
              autoFocus
              size="small"
              label="Account #"
              value={mapAcctInput}
              onChange={(e) => setMapAcctInput(e.target.value.replace(/[^0-9]/g, ''))}
              onKeyDown={(e) => {
                if (e.key === 'Enter') lookupAccountNo()
              }}
              sx={{ flex: 1 }}
            />
            <Button
              variant="outlined"
              onClick={lookupAccountNo}
              disabled={!mapAcctInput || mapLookupBusy}
              startIcon={mapLookupBusy ? <CircularProgress size={14} /> : null}
            >
              Look up
            </Button>
          </Stack>
          {mapLookupError && (
            <Alert severity="error" sx={{ mt: 2 }}>
              {mapLookupError}
            </Alert>
          )}
          {mapLookup && (
            <Alert severity="success" sx={{ mt: 2 }}>
              <strong>{mapLookup.fullName}</strong>
              {mapLookup.birthDate && (
                <> · DOB {new Date(mapLookup.birthDate).toLocaleDateString('en-US')}</>
              )}
              <br />
              <Typography variant="caption" color="text.secondary">
                PSChiro PatientID {mapLookup.patientId} · Account #{mapLookup.accountNo ?? '—'}
              </Typography>
            </Alert>
          )}
        </DialogContent>
        <DialogActions>
          <Button onClick={closeMapDialog}>Cancel</Button>
          <Button
            variant="contained"
            color="primary"
            disabled={!mapLookup}
            onClick={confirmMap}
          >
            Confirm &amp; resolve {mapTarget?.rows.length} line
            {mapTarget?.rows.length === 1 ? '' : 's'}
          </Button>
        </DialogActions>
      </Dialog>

      <Dialog open={confirmOpen} onClose={() => setConfirmOpen(false)} maxWidth="xs" fullWidth>
        <DialogTitle>Apply EOB to PSChiro?</DialogTitle>
        <DialogContent>
          <DialogContentText>
            This will write to PSChiro <strong>for real</strong>: {result?.matched.length ?? 0}{' '}
            matched charge line{(result?.matched.length ?? 0) === 1 ? '' : 's'} will be stamped
            with the EOB's Paid + Write-off amounts in a single transaction. Already-posted lines
            and over-payment cases are skipped automatically.
          </DialogContentText>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setConfirmOpen(false)}>Cancel</Button>
          <Button variant="contained" color="primary" onClick={apply}>
            Apply
          </Button>
        </DialogActions>
      </Dialog>

      {result && (
        <>
          <Stack direction="row" spacing={1} sx={{ mb: 2, flexWrap: 'wrap', gap: 1 }}>
            <Chip
              icon={<CheckCircle />}
              color="success"
              label={`${cleanMatched.length} matched`}
              sx={{ fontWeight: 600 }}
            />
            {skipMatched.length > 0 && (
              <Chip
                color="default"
                label={`${skipMatched.length} already posted`}
                sx={{ fontWeight: 600 }}
              />
            )}
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

          {/* ---- Matched (collapsible, grouped by patient) ---- */}
          {cleanMatched.length > 0 && (() => {
            // Show EOB-reported amounts (paidAmount / writeOffAmount on each
            // line). Avoid "current → proposed" math here — the proposed math
            // reflects our INCOMPLETE write logic, not what CT actually does
            // on EOB post. Display the carrier's reported numbers so the
            // operator sees facts; the Apply rebuild will introduce a real
            // "what will be written" preview once it mirrors CT's behavior.
            const totalEobPaid = cleanMatched.reduce((s, m) => s + m.line.paidAmount, 0)
            const totalEobWO = cleanMatched.reduce((s, m) => s + m.line.writeOffAmount, 0)
            const sorted = [...cleanMatched].sort(
              (a, b) =>
                (b.line.paidAmount + b.line.writeOffAmount) -
                (a.line.paidAmount + a.line.writeOffAmount),
            )
            const byPatient = groupBy(sorted, (m) => m.line.patientName)
            return (
              <Accordion defaultExpanded sx={{ mb: 1 }}>
                <AccordionSummary expandIcon={<ExpandMore />}>
                  <Stack direction="row" spacing={2} alignItems="center" sx={{ width: '100%' }}>
                    <CheckCircle color="success" />
                    <Typography variant="h6" sx={{ fontWeight: 600 }}>
                      Matched ({cleanMatched.length})
                    </Typography>
                    <Box sx={{ flexGrow: 1 }} />
                    <Typography variant="body2" color="text.secondary">
                      {byPatient.length} patient{byPatient.length === 1 ? '' : 's'} ·{' '}
                      EOB Paid {money.format(totalEobPaid)} · Write-off {money.format(totalEobWO)}
                    </Typography>
                  </Stack>
                </AccordionSummary>
                <AccordionDetails sx={{ p: 0 }}>
                  {byPatient.map(({ key: patient, rows }) => {
                    const patEobPaid = rows.reduce((s, m) => s + m.line.paidAmount, 0)
                    const patEobWO = rows.reduce((s, m) => s + m.line.writeOffAmount, 0)
                    return (
                      <Accordion key={patient} disableGutters elevation={0} defaultExpanded={byPatient.length === 1}>
                        <AccordionSummary
                          expandIcon={<ExpandMore />}
                          sx={{ bgcolor: 'action.hover', borderTop: 1, borderColor: 'divider' }}
                        >
                          <Stack direction="row" spacing={1.5} alignItems="center" sx={{ width: '100%' }}>
                            <Person fontSize="small" color="action" />
                            <Typography sx={{ fontWeight: 600 }}>{patient}</Typography>
                            <Chip size="small" label={`${rows.length} line${rows.length === 1 ? '' : 's'}`} />
                            <Box sx={{ flexGrow: 1 }} />
                            <Typography variant="caption" color="text.secondary">
                              EOB Paid {money.format(patEobPaid)} · W/O {money.format(patEobWO)}
                            </Typography>
                          </Stack>
                        </AccordionSummary>
                        <AccordionDetails sx={{ p: 0 }}>
                          <TableContainer>
                            <Table size="small">
                              <TableHead>
                                <TableRow>
                                  <TableCell>DOS</TableCell>
                                  <TableCell>CPT</TableCell>
                                  <TableCell align="right">Charge</TableCell>
                                  <TableCell align="right">EOB Paid</TableCell>
                                  <TableCell align="right">EOB Write-off</TableCell>
                                  <TableCell>Reason</TableCell>
                                  <TableCell align="right">TranId</TableCell>
                                </TableRow>
                              </TableHead>
                              <TableBody>
                                {rows.map((m, i) => (
                                  <TableRow key={i}>
                                    <TableCell>{fmtDate(m.line.dateOfService)}</TableCell>
                                    <TableCell>{m.line.proceduralCode}</TableCell>
                                    <TableCell align="right">{money.format(m.line.individualCharge)}</TableCell>
                                    <TableCell align="right">{money.format(m.line.paidAmount)}</TableCell>
                                    <TableCell align="right">{money.format(m.line.writeOffAmount)}</TableCell>
                                    <TableCell>
                                      <Typography variant="caption">
                                        {m.proposed.reasonCode}
                                        {m.proposed.reasonDescription ? ` · ${m.proposed.reasonDescription}` : ''}
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
                        </AccordionDetails>
                      </Accordion>
                    )
                  })}
                </AccordionDetails>
              </Accordion>
            )
          })()}

          {/* ---- Already posted (would skip on apply) ----
              Lines that match by patient + DOS + CPT but the existing
              charge totals already equal/exceed what the EOB says, or the
              proposed total would over-pay. Apply's guards skip these, so
              we surface them separately rather than letting them pollute
              the Matched count with scary-looking doubled math. */}
          {skipMatched.length > 0 && (() => {
            const byPatient = groupBy(skipMatched, (m) => m.line.patientName)
            return (
              <Accordion sx={{ mb: 1 }}>
                <AccordionSummary expandIcon={<ExpandMore />}>
                  <Stack direction="row" spacing={2} alignItems="center" sx={{ width: '100%' }}>
                    <CheckCircle color="disabled" />
                    <Typography variant="h6" sx={{ fontWeight: 600 }}>
                      Already posted — would be skipped ({skipMatched.length})
                    </Typography>
                    <Box sx={{ flexGrow: 1 }} />
                    <Typography variant="body2" color="text.secondary">
                      {byPatient.length} patient{byPatient.length === 1 ? '' : 's'} · no action needed
                    </Typography>
                  </Stack>
                </AccordionSummary>
                <AccordionDetails sx={{ p: 0 }}>
                  {byPatient.map(({ key: patient, rows }) => (
                    <Accordion key={patient} disableGutters elevation={0}>
                      <AccordionSummary
                        expandIcon={<ExpandMore />}
                        sx={{ bgcolor: 'action.hover', borderTop: 1, borderColor: 'divider' }}
                      >
                        <Stack direction="row" spacing={1.5} alignItems="center" sx={{ width: '100%' }}>
                          <Person fontSize="small" color="action" />
                          <Typography sx={{ fontWeight: 600 }}>{patient}</Typography>
                          <Chip size="small" label={`${rows.length} line${rows.length === 1 ? '' : 's'}`} />
                        </Stack>
                      </AccordionSummary>
                      <AccordionDetails sx={{ p: 0 }}>
                        <TableContainer>
                          <Table size="small">
                            <TableHead>
                              <TableRow>
                                <TableCell>DOS</TableCell>
                                <TableCell>CPT</TableCell>
                                <TableCell align="right">Charge</TableCell>
                                <TableCell align="right">Currently Paid</TableCell>
                                <TableCell align="right">Currently W/O</TableCell>
                                <TableCell>Why skipped</TableCell>
                              </TableRow>
                            </TableHead>
                            <TableBody>
                              {rows.map((m, i) => {
                                const cur = m.proposed.currentPriPaidAmt + m.proposed.currentWOAmt
                                const next = m.proposed.proposedPriPaidAmt + m.proposed.proposedWOAmt
                                const reason =
                                  cur >= m.match.tranAmt - 0.01
                                    ? 'Charge already fully resolved in PSChiro'
                                    : `Would over-pay (proposed ${money.format(next)} > charge ${money.format(m.match.tranAmt)})`
                                return (
                                  <TableRow key={i}>
                                    <TableCell>{fmtDate(m.line.dateOfService)}</TableCell>
                                    <TableCell>{m.line.proceduralCode}</TableCell>
                                    <TableCell align="right">{money.format(m.match.tranAmt)}</TableCell>
                                    <TableCell align="right">{money.format(m.proposed.currentPriPaidAmt)}</TableCell>
                                    <TableCell align="right">{money.format(m.proposed.currentWOAmt)}</TableCell>
                                    <TableCell>
                                      <Typography variant="caption">{reason}</Typography>
                                    </TableCell>
                                  </TableRow>
                                )
                              })}
                            </TableBody>
                          </Table>
                        </TableContainer>
                      </AccordionDetails>
                    </Accordion>
                  ))}
                </AccordionDetails>
              </Accordion>
            )
          })()}

          {/* ---- Ambiguous (collapsible, grouped by patient) ---- */}
          {result.ambiguous.length > 0 && (() => {
            const byPatient = groupBy(result.ambiguous, (a) => a.line.patientName)
            return (
              <Accordion defaultExpanded={result.matched.length === 0} sx={{ mb: 1 }}>
                <AccordionSummary expandIcon={<ExpandMore />}>
                  <Stack direction="row" spacing={2} alignItems="center" sx={{ width: '100%' }}>
                    <HelpOutline color="warning" />
                    <Typography variant="h6" sx={{ fontWeight: 600 }}>
                      Ambiguous — needs review ({result.ambiguous.length})
                    </Typography>
                    <Box sx={{ flexGrow: 1 }} />
                    <Typography variant="body2" color="text.secondary">
                      {byPatient.length} patient{byPatient.length === 1 ? '' : 's'}
                    </Typography>
                  </Stack>
                </AccordionSummary>
                <AccordionDetails sx={{ p: 0 }}>
                  {byPatient.map(({ key: patient, rows }) => (
                    <Accordion key={patient} disableGutters elevation={0} defaultExpanded={byPatient.length === 1}>
                      <AccordionSummary
                        expandIcon={<ExpandMore />}
                        sx={{ bgcolor: 'action.hover', borderTop: 1, borderColor: 'divider' }}
                      >
                        <Stack direction="row" spacing={1.5} alignItems="center" sx={{ width: '100%' }}>
                          <Person fontSize="small" color="action" />
                          <Typography sx={{ fontWeight: 600 }}>{patient}</Typography>
                          <Chip size="small" label={`${rows.length} line${rows.length === 1 ? '' : 's'}`} />
                        </Stack>
                      </AccordionSummary>
                      <AccordionDetails sx={{ p: 0 }}>
                        <TableContainer>
                          <Table size="small">
                            <TableHead>
                              <TableRow>
                                <TableCell>DOS</TableCell>
                                <TableCell>CPT</TableCell>
                                <TableCell align="right">EOB Charge</TableCell>
                                <TableCell>Candidates</TableCell>
                                <TableCell>Reason</TableCell>
                              </TableRow>
                            </TableHead>
                            <TableBody>
                              {rows.map((a, i) => (
                                <TableRow key={i}>
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
                      </AccordionDetails>
                    </Accordion>
                  ))}
                </AccordionDetails>
              </Accordion>
            )
          })()}

          {/* ---- Unmatched (collapsible, grouped by patient) ---- */}
          {result.unmatched.length > 0 && (() => {
            const byPatient = groupBy(result.unmatched, (u) => u.line.patientName)
            return (
              <Accordion defaultExpanded={result.matched.length === 0 && result.ambiguous.length === 0} sx={{ mb: 1 }}>
                <AccordionSummary expandIcon={<ExpandMore />}>
                  <Stack direction="row" spacing={2} alignItems="center" sx={{ width: '100%' }}>
                    <ErrorOutline color="error" />
                    <Typography variant="h6" sx={{ fontWeight: 600 }}>
                      Unmatched ({result.unmatched.length})
                    </Typography>
                    <Box sx={{ flexGrow: 1 }} />
                    <Typography variant="body2" color="text.secondary">
                      {byPatient.length} patient{byPatient.length === 1 ? '' : 's'}
                    </Typography>
                  </Stack>
                </AccordionSummary>
                <AccordionDetails sx={{ p: 0 }}>
                  {byPatient.map(({ key: patient, rows }) => {
                    // Suggestions are per-line on the server, but in practice
                    // every line in a patient group has the same suggestion
                    // set (same name → same fuzzy matches). Use the first
                    // row's suggestions for the group-level chips.
                    const suggestions = rows[0]?.suggestions ?? []
                    return (
                      <Accordion
                        key={patient}
                        disableGutters
                        elevation={0}
                        defaultExpanded={byPatient.length === 1}
                      >
                        <AccordionSummary
                          expandIcon={<ExpandMore />}
                          sx={{ bgcolor: 'action.hover', borderTop: 1, borderColor: 'divider' }}
                        >
                          <Stack
                            direction="row"
                            spacing={1.5}
                            alignItems="center"
                            sx={{ width: '100%', flexWrap: 'wrap', rowGap: 0.5 }}
                          >
                            <Person fontSize="small" color="action" />
                            <Typography sx={{ fontWeight: 600 }}>{patient}</Typography>
                            <Chip size="small" label={`${rows.length} line${rows.length === 1 ? '' : 's'}`} />
                            {suggestions.length > 0 && (
                              <>
                                <Typography variant="caption" color="text.secondary" sx={{ ml: 1 }}>
                                  Did you mean:
                                </Typography>
                                {suggestions.map((s) => (
                                  <Chip
                                    key={s.patientId}
                                    size="small"
                                    clickable
                                    variant="outlined"
                                    label={`${s.fullName} (#${s.patientId})`}
                                    onClick={(e) => {
                                      // Don't toggle the Accordion when
                                      // clicking the suggestion chip.
                                      e.stopPropagation()
                                      resolvePatientGroup(rows, s.patientId)
                                    }}
                                  />
                                ))}
                              </>
                            )}
                            {/* Always-available manual override: paste a CT
                                AccountNo, confirm the patient, resolve all
                                rows in the group. Works whether or not we
                                surfaced fuzzy suggestions. */}
                            <Button
                              size="small"
                              variant="outlined"
                              startIcon={<LinkIcon fontSize="small" />}
                              onClick={(e) => {
                                e.stopPropagation()
                                openMapDialog(patient, rows)
                              }}
                              sx={{ ml: 'auto' }}
                            >
                              Map to CT #
                            </Button>
                          </Stack>
                        </AccordionSummary>
                        <AccordionDetails sx={{ p: 0 }}>
                          <TableContainer>
                            <Table size="small">
                              <TableHead>
                                <TableRow>
                                  <TableCell>DOS</TableCell>
                                  <TableCell>CPT</TableCell>
                                  <TableCell align="right">Charge</TableCell>
                                  <TableCell>Reason</TableCell>
                                </TableRow>
                              </TableHead>
                              <TableBody>
                                {rows.map((u, i) => (
                                  <TableRow key={i}>
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
                        </AccordionDetails>
                      </Accordion>
                    )
                  })}
                </AccordionDetails>
              </Accordion>
            )
          })()}

          <Alert severity="warning" sx={{ mb: 2 }}>
            <strong>Apply is temporarily disabled.</strong> The current write logic only
            updates charge totals + BilledCharges; ChiroTouch's real EOB posting also
            creates a PaymentClaims envelope and per-line payment Transactions rows. Re-enabling
            once the full posting hierarchy lands so postings show up correctly in CT's payment
            history and reconciliation reports. Preview + match/resolve still work for review.
          </Alert>
        </>
      )}
    </Box>
  )
}

export default EobPreview
