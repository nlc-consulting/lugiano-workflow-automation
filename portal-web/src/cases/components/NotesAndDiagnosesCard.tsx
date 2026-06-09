import { useState } from 'react'
import {
  useNotify,
  useRefresh,
  useShowContext,
  type RaRecord,
} from 'react-admin'
import {
  Box,
  Button,
  Chip,
  Tab,
  Tabs,
  Tooltip,
  Typography,
} from '@mui/material'
import ReplyAll from '@mui/icons-material/ReplyAll'
import SectionCard from './SectionCard'
import { formatVisitTime } from './formatters'
import KickbackModal from '../KickbackModal'
import ScrubPanel from '../ScrubPanel'

type NoteVisit = {
  visitId?: number | null
  visitTime?: string | null
  visitDoctor?: string | null
  visitsSameDay?: number | null
  matchScore?: number | null
  matchReasons?: string[] | null
}

type NoteRow = NoteVisit & {
  id: number
  noteDate?: string | null
  doctor?: string | null
  plainText?: string | null
}

type DiagnosisItem = { code: string; description?: string | null }

const scoreColor = (score: number): 'success' | 'warning' | 'error' =>
  score >= 85 ? 'success' : score >= 50 ? 'warning' : 'error'

// Render the visit-match chip directly from a note object (no record context
// required) so it can sit inside the tab metadata row.
const renderVisitChip = (note: NoteVisit) => {
  const score = note.matchScore ?? 0
  const reasons = note.matchReasons ?? []

  if (!note.visitId) {
    return <Chip size="small" color="error" variant="outlined" label="No visit found" />
  }

  const time = formatVisitTime(note.visitTime)
  const base = note.visitDoctor ? `${time} — ${note.visitDoctor}` : time
  const label = `${base} · ${score}`

  const tooltipBody =
    reasons.length > 0 ? (
      <Box>
        <Typography variant="caption" sx={{ display: 'block', fontWeight: 600 }}>
          Match score: {score}
        </Typography>
        {reasons.map((r) => (
          <Typography key={r} variant="caption" sx={{ display: 'block' }}>
            • {r}
          </Typography>
        ))}
      </Box>
    ) : (
      <Typography variant="caption">Clean match (score {score})</Typography>
    )

  return (
    <Tooltip title={tooltipBody} arrow>
      <Chip size="small" color={scoreColor(score)} variant="outlined" label={label} />
    </Tooltip>
  )
}

// Mirrors ChiroTouch's chart-note view: the patient's diagnosis list stays
// pinned on the left while notes are reviewed as horizontal tabs on the right
// (one tab per loaded note). Switching tabs changes the note content; the
// diagnosis list doesn't move. Per-visit charge detail lives in the Ledger
// card below.
const NotesAndDiagnosesCard = () => {
  const { record } = useShowContext<
    RaRecord & { patientId: number; notes?: NoteRow[]; diagnoses?: DiagnosisItem[] }
  >()
  const notes = record?.notes ?? []
  const diagnoses = record?.diagnoses ?? []
  const [activeNoteId, setActiveNoteId] = useState<number | null>(null)
  const [kickbackOpen, setKickbackOpen] = useState(false)
  const notify = useNotify()
  const refresh = useRefresh()

  const currentNote = notes.find((n) => n.id === activeNoteId) ?? notes[0]

  return (
    <SectionCard
      title="Notes & diagnoses"
      caption="Diagnoses stay pinned on the left while you flip between notes — mirroring ChiroTouch's chart-note view."
    >
      <Box sx={{ display: 'flex', gap: 2, alignItems: 'flex-start' }}>
        {/* Left rail: persistent patient diagnoses. */}
        <Box sx={{ width: 280, flexShrink: 0 }}>
          <Typography
            variant="overline"
            color="text.secondary"
            sx={{ display: 'block', mb: 1 }}
          >
            Patient diagnoses ({diagnoses.length})
          </Typography>
          {diagnoses.length === 0 ? (
            <Typography variant="body2" color="text.secondary">
              No ICD-10 codes found in loaded notes.
            </Typography>
          ) : (
            <Box sx={{ display: 'flex', flexDirection: 'column' }}>
              {diagnoses.map((d) => (
                <Box
                  key={d.code}
                  sx={{ py: 0.75, borderBottom: '1px solid', borderColor: 'divider' }}
                >
                  <Typography variant="body2" sx={{ fontFamily: 'monospace', fontWeight: 700 }}>
                    {d.code}
                  </Typography>
                  {d.description && (
                    <Typography variant="caption" color="text.secondary">
                      {d.description}
                    </Typography>
                  )}
                </Box>
              ))}
            </Box>
          )}
        </Box>

        {/* Right: tabs across the top, active note content below. */}
        <Box
          sx={{
            flex: 1,
            minWidth: 0,
            borderLeft: '1px solid',
            borderColor: 'divider',
            pl: 2,
          }}
        >
          {notes.length === 0 ? (
            <Typography variant="body2" color="text.secondary" sx={{ py: 2 }}>
              No recent notes for this patient.
            </Typography>
          ) : (
            <>
              <Tabs
                value={currentNote?.id ?? false}
                onChange={(_, id: number) => setActiveNoteId(id)}
                variant="scrollable"
                scrollButtons="auto"
                sx={{ borderBottom: 1, borderColor: 'divider' }}
              >
                {notes.map((n) => (
                  <Tab
                    key={n.id}
                    value={n.id}
                    label={
                      n.noteDate
                        ? new Date(n.noteDate).toLocaleDateString('en-US', {
                            timeZone: 'America/New_York',
                          })
                        : `Note ${n.id}`
                    }
                  />
                ))}
              </Tabs>

              {currentNote && (
                <Box sx={{ mt: 2 }}>
                  <Box
                    sx={{
                      display: 'flex',
                      justifyContent: 'space-between',
                      alignItems: 'center',
                      gap: 1,
                      mb: 1.5,
                    }}
                  >
                    <Typography variant="body2" color="text.secondary">
                      {currentNote.doctor ?? '—'}
                    </Typography>
                    <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
                      {renderVisitChip(currentNote)}
                      <Button
                        size="small"
                        variant="outlined"
                        color="primary"
                        startIcon={<ReplyAll fontSize="small" />}
                        onClick={() => setKickbackOpen(true)}
                      >
                        Send back to doctor
                      </Button>
                    </Box>
                  </Box>
                  {currentNote.plainText ? (
                    <Box
                      sx={{
                        whiteSpace: 'pre-wrap',
                        fontFamily: 'monospace',
                        fontSize: 12,
                        bgcolor: 'action.hover',
                        p: 2,
                        borderRadius: 1,
                        maxHeight: 600,
                        overflowY: 'auto',
                      }}
                    >
                      {currentNote.plainText}
                    </Box>
                  ) : (
                    <Typography variant="body2" color="text.secondary">
                      Note text wasn't reconstructed for this row (the detail view
                      caps RTF reconstruction at the 3 most recent notes for performance).
                    </Typography>
                  )}

                  <ScrubPanel
                    patientId={(record?.patientId as number) ?? 0}
                    chartNoteId={currentNote.id}
                  />
                </Box>
              )}
            </>
          )}
        </Box>
      </Box>

      {currentNote && (
        <KickbackModal
          open={kickbackOpen}
          onClose={() => setKickbackOpen(false)}
          patientId={(record?.patientId as number) ?? 0}
          chartNoteId={currentNote.id}
          noteDateLabel={
            currentNote.noteDate
              ? new Date(currentNote.noteDate).toLocaleDateString('en-US', {
                  timeZone: 'America/New_York',
                })
              : `Note ${currentNote.id}`
          }
          onSent={(msg) => {
            notify(msg, { type: 'success' })
            refresh()
          }}
        />
      )}
    </SectionCard>
  )
}

export default NotesAndDiagnosesCard
