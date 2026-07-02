import { useEffect, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  Checkbox,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControlLabel,
  Stack,
  TextField,
  Typography,
} from '@mui/material'

const WORKFLOW_API = import.meta.env.VITE_WORKFLOW_API_URL || '/workflow-api'

// The six predefined issues that mirror what Jacob checks for today; the AI
// scrubber will preselect these in the next epic but the manual list is the
// same. Free-text "Other" handled by ReviewerComments.
const MISSING_ITEM_PRESETS: string[] = [
  'Subjective section missing or incomplete',
  'Objective section missing or incomplete',
  'Assessment section missing or incomplete',
  '"In my opinion" line missing',
  'Treatment plan missing or incomplete',
  'Primary treatment missing',
]

type NoteDoctor = {
  id: number
  chiroTouchDoctorId: number
  fullName?: string | null
  credentials?: string | null
  email?: string | null
  isActive?: boolean
  isPrimary?: boolean
}

type Props = {
  open: boolean
  onClose: () => void
  patientId: number
  chartNoteId: number
  noteDateLabel: string
  onSent: (message: string) => void
}

const KickbackModal = ({ open, onClose, patientId, chartNoteId, noteDateLabel, onSent }: Props) => {
  const [loading, setLoading] = useState(false)
  const [doctors, setDoctors] = useState<NoteDoctor[] | null>(null)
  const [missingItems, setMissingItems] = useState<Set<string>>(new Set())
  const [reviewerComments, setReviewerComments] = useState('')
  const [error, setError] = useState<string | null>(null)

  // Fetch the doctor(s) on this note when the modal opens.
  useEffect(() => {
    if (!open) return
    setError(null)
    setDoctors(null)
    setMissingItems(new Set())
    setReviewerComments('')

    fetch(`${WORKFLOW_API}/cases/${patientId}/notes/${chartNoteId}/doctors`)
      .then((r) => (r.ok ? r.json() : Promise.reject(`HTTP ${r.status}`)))
      .then((data: { doctors: NoteDoctor[] }) => setDoctors(data.doctors ?? []))
      .catch((e) => setError(typeof e === 'string' ? e : 'Failed to load doctor info.'))
  }, [open, patientId, chartNoteId])

  const primary = doctors?.[0]
  const canSend = !loading && !!primary

  const toggleItem = (item: string) => {
    setMissingItems((prev) => {
      const next = new Set(prev)
      if (next.has(item)) next.delete(item)
      else next.add(item)
      return next
    })
  }

  const handleSend = async () => {
    if (!primary) return
    setLoading(true)
    setError(null)
    try {
      const res = await fetch(
        `${WORKFLOW_API}/cases/${patientId}/notes/${chartNoteId}/kickback`,
        {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            recipientDoctorIds: [primary.id],
            overrideEmail: null,
            saveOverrideAsDefault: false,
            missingItems: Array.from(missingItems),
            reviewerComments: reviewerComments.trim() || null,
          }),
        },
      )
      if (!res.ok) {
        const body = await res.json().catch(() => ({}))
        throw new Error(body.error ?? `HTTP ${res.status}`)
      }
      const data = await res.json()
      const name = `${primary.fullName ?? 'the doctor'}${primary.credentials ? `, ${primary.credentials}` : ''}`
      onSent(
        data.state === 'Escalated'
          ? `Round cap reached (${data.roundNumber}). Case escalated to manual review.`
          : `Sent back to ${name} — now in their Doctor Review (round ${data.roundNumber}).`,
      )
      onClose()
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Send failed.')
    } finally {
      setLoading(false)
    }
  }

  return (
    <Dialog open={open} onClose={onClose} maxWidth="sm" fullWidth>
      <DialogTitle>Send note back to doctor</DialogTitle>
      <DialogContent dividers>
        <Stack spacing={2}>
          <Typography variant="body2" color="text.secondary">
            Visit: <b>{noteDateLabel}</b>
          </Typography>

          {error && <Alert severity="error">{error}</Alert>}

          {!doctors && !error && (
            <Typography variant="body2" color="text.secondary">Loading doctor info…</Typography>
          )}

          {doctors && doctors.length === 0 && (
            <Alert severity="warning">
              No doctor is linked to this note. We can't send a correction request without a recipient.
            </Alert>
          )}

          {primary && (
            <Box
              sx={{
                p: 1.5,
                bgcolor: 'action.hover',
                borderRadius: 1,
                border: '1px solid',
                borderColor: 'divider',
              }}
            >
              <Typography variant="overline" color="text.secondary" sx={{ display: 'block' }}>
                Recipient
              </Typography>
              <Typography variant="body2" sx={{ fontWeight: 600 }}>
                {primary.fullName ?? '—'}
                {primary.credentials ? `, ${primary.credentials}` : ''}
              </Typography>
              <Typography variant="caption" color="text.secondary">
                Appears in this doctor's Doctor Review — no email sent.
              </Typography>
            </Box>
          )}

          <Box>
            <Typography variant="overline" color="text.secondary" sx={{ display: 'block', mb: 0.5 }}>
              What's missing or wrong?
            </Typography>
            <Stack>
              {MISSING_ITEM_PRESETS.map((item) => (
                <FormControlLabel
                  key={item}
                  control={
                    <Checkbox
                      size="small"
                      checked={missingItems.has(item)}
                      onChange={() => toggleItem(item)}
                    />
                  }
                  label={<Typography variant="body2">{item}</Typography>}
                />
              ))}
            </Stack>
          </Box>

          <TextField
            label="Notes to the doctor"
            placeholder="Anything else the doctor should know — context, specific things to fix, urgency…"
            multiline
            minRows={3}
            value={reviewerComments}
            onChange={(e) => setReviewerComments(e.target.value)}
            fullWidth
          />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} disabled={loading}>
          Cancel
        </Button>
        <Button
          variant="contained"
          color="primary"
          disabled={!canSend}
          onClick={handleSend}
        >
          {loading ? 'Sending…' : 'Send back to doctor'}
        </Button>
      </DialogActions>
    </Dialog>
  )
}

export default KickbackModal
