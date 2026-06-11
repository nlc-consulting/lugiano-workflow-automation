import { useState } from 'react'
import {
  Datagrid,
  FunctionField,
  List,
  TextField,
  useNotify,
  useRefresh,
  type RaRecord,
} from 'react-admin'
import {
  Alert,
  Box,
  Button,
  Chip,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
  TextField as MuiTextField,
  Typography,
} from '@mui/material'
import CheckCircle from '@mui/icons-material/CheckCircle'
import EditNote from '@mui/icons-material/EditNote'
import EstDateTimeField from '../cases/EstDateTimeField'

const WORKFLOW_API = import.meta.env.VITE_WORKFLOW_API_URL || '/workflow-api'

type ReviewRow = RaRecord & {
  patientId: number
  firstName?: string
  lastName?: string
  latestScrubAt?: string | null
  summary?: string | null
}

type CorrectionResponse = {
  doctorNoteId: number
  verdict: 'pass' | 'needs_review' | 'fail'
  summary?: string | null
  ranAt: string
}

const VERDICT_LABELS: Record<string, string> = {
  pass: 'Pass',
  needs_review: 'Needs review',
  fail: 'Fail',
}
const VERDICT_COLORS: Record<string, 'success' | 'warning' | 'error'> = {
  pass: 'success',
  needs_review: 'warning',
  fail: 'error',
}

// Modal extracted so the list keeps a single mount and we don't re-fire data
// fetches when the user opens/closes it.
const CorrectionModal = ({
  row,
  open,
  onClose,
}: {
  row: ReviewRow | null
  open: boolean
  onClose: () => void
}) => {
  const [text, setText] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [result, setResult] = useState<CorrectionResponse | null>(null)
  const [error, setError] = useState<string | null>(null)
  const notify = useNotify()
  const refresh = useRefresh()

  const reset = () => {
    setText('')
    setSubmitting(false)
    setResult(null)
    setError(null)
  }
  const handleClose = () => {
    reset()
    onClose()
  }
  const handleDone = () => {
    if (result) refresh()
    handleClose()
  }

  const submit = async () => {
    if (!row || !text.trim()) return
    setSubmitting(true)
    setError(null)
    try {
      const res = await fetch(`${WORKFLOW_API}/cases/${row.patientId}/doctor-notes`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
        // originalDoctorNoteId anchors the PSChiro writeback to the failing
        // note's visit + doctor so the correction lands on the same DOS in
        // ChiroTouch's appointment-anchored UI.
        body: JSON.stringify({ text: text.trim(), originalDoctorNoteId: row.doctorNoteId }),
      })
      if (!res.ok) {
        const body = await res.json().catch(() => ({}))
        throw new Error(body.error ?? `HTTP ${res.status}`)
      }
      await res.json().catch(() => null) // drain response; we don't need the verdict
      // Doctor is done — they don't need to see the new verdict. If the
      // correction still doesn't pass, the case stays in the review queue
      // for a human reviewer to handle next. Auto-close the modal so the
      // doctor doesn't have to dismiss it manually.
      notify('Correction submitted.', { type: 'success' })
      refresh()
      handleClose()
      return
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Submit failed.')
    } finally {
      setSubmitting(false)
    }
  }

  if (!row) return null

  return (
    <Dialog open={open} onClose={handleClose} maxWidth="md" fullWidth>
      <DialogTitle>
        Correct note — {row.lastName}, {row.firstName}{' '}
        <Typography component="span" variant="caption" color="text.secondary">
          (#{row.patientId})
        </Typography>
      </DialogTitle>
      <DialogContent>
        {row.summary && (
          <Alert severity="warning" sx={{ mb: 2 }}>
            <Typography variant="caption" sx={{ fontWeight: 600, display: 'block' }}>
              AI scrubber reason
            </Typography>
            {row.summary}
          </Alert>
        )}

        {!result ? (
          <>
            <DialogContentText sx={{ mb: 1.5 }}>
              Write the corrected note below. On submit, the system re-scrubs
              immediately and the new verdict shows here.
            </DialogContentText>
            <MuiTextField
              autoFocus
              multiline
              minRows={8}
              fullWidth
              placeholder="Subjective: ...\nObjective: ...\nAssessment: In my opinion, ...\nPlan: ..."
              value={text}
              onChange={(e) => setText(e.target.value)}
              disabled={submitting}
            />
            {error && (
              <Alert severity="error" sx={{ mt: 2 }}>
                {error}
              </Alert>
            )}
          </>
        ) : (
          <Box sx={{ py: 2, display: 'flex', alignItems: 'center', gap: 1.5 }}>
            <CheckCircle color="success" />
            <Box>
              <Typography variant="h6">Correction submitted</Typography>
              <Typography variant="body2" color="text.secondary">
                The case will be re-reviewed. If issues remain, a human reviewer will follow up.
              </Typography>
            </Box>
          </Box>
        )}
      </DialogContent>
      <DialogActions>
        {!result ? (
          <>
            <Button onClick={handleClose} disabled={submitting}>
              Cancel
            </Button>
            <Button
              variant="contained"
              onClick={submit}
              disabled={submitting || !text.trim()}
              startIcon={submitting ? <CircularProgress size={16} /> : <EditNote />}
            >
              {submitting ? 'Submitting & re-scrubbing…' : 'Submit corrected note'}
            </Button>
          </>
        ) : (
          <Button variant="contained" onClick={handleDone}>
            Done
          </Button>
        )}
      </DialogActions>
    </Dialog>
  )
}

// Doctor's queue — failed scrubs, click a row to open the correction modal.
// Demo-shortcut: no real doctor identity yet, every doctor sees every flagged
// case. Real role scoping is task #40.
const DoctorReviewList = () => {
  const [openFor, setOpenFor] = useState<ReviewRow | null>(null)

  return (
    <>
      <List
        title="Doctor View"
        resource="scrub-review"
        sort={{ field: 'latestScrubAt', order: 'DESC' }}
        exporter={false}
        actions={false}
      >
        <Datagrid
          bulkActionButtons={false}
          rowClick={(_id, _resource, record) => {
            setOpenFor(record as ReviewRow)
            return false
          }}
        >
          <TextField source="patientId" label="Patient ID" />
          <FunctionField
            label="Patient"
            render={(r: ReviewRow) =>
              `${r.lastName ?? ''}, ${r.firstName ?? ''}`.replace(/^, |, $/, '').trim()
            }
          />
          <EstDateTimeField source="latestScrubAt" label="Last scrub" />
          <TextField source="summary" label="Reason" />
          <FunctionField
            label=""
            render={(r: ReviewRow) => (
              <Button
                size="small"
                variant="outlined"
                color="primary"
                startIcon={<EditNote fontSize="small" />}
                onClick={(e) => {
                  e.stopPropagation()
                  setOpenFor(r)
                }}
              >
                Correct & Done
              </Button>
            )}
          />
        </Datagrid>
      </List>
      <CorrectionModal
        row={openFor}
        open={openFor !== null}
        onClose={() => setOpenFor(null)}
      />
    </>
  )
}

export default DoctorReviewList
