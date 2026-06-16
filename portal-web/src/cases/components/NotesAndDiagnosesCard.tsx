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
import { formatShortDate, formatVisitTime } from './formatters'
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

type ScrubSnapshot = {
  verdict?: 'pass' | 'needs_review' | 'fail' | null
  summary?: string | null
  ranAt?: string | null
}

type NoteRow = NoteVisit & {
  id: number
  // 'chart' = sourced from PSChiro ChartNotes (the normal case)
  // 'portal' = doctor-authored correction made through our portal (no ChiroTouch row yet)
  source?: 'chart' | 'portal'
  // Our internal DoctorNote.Id — present for both chart and portal notes.
  // The per-tab ScrubPanel keys off this for /notes/{id}/scrub endpoints.
  doctorNoteId?: number | null
  portalNoteId?: number
  noteDate?: string | null
  doctor?: string | null
  plainText?: string | null
  diagnoses?: DiagnosisItem[] | null
  latestScrub?: ScrubSnapshot | null
}

type DiagnosisItem = { code: string; description?: string | null }

const SCRUB_LABELS: Record<string, string> = {
  pass: 'Pass',
  needs_review: 'Needs review',
  fail: 'Fail',
}
const SCRUB_COLORS: Record<string, 'success' | 'warning' | 'error'> = {
  pass: 'success',
  needs_review: 'warning',
  fail: 'error',
}

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
    RaRecord & { patientId: number; notes?: NoteRow[] }
  >()
  // ChiroTouch is appointment-centric: it only surfaces notes attached to a
  // visit. We pull every ChartNotes row, so drop notes that matched no
  // appointment (orphaned rows — e.g. stray test notes) to show the same set
  // ChiroTouch does.
  const notes = (record?.notes ?? []).filter((n) => n.visitId != null)
  const [activeNoteId, setActiveNoteId] = useState<number | null>(null)
  const [kickbackOpen, setKickbackOpen] = useState(false)
  const notify = useNotify()
  const refresh = useRefresh()

  const currentNote = notes.find((n) => n.id === activeNoteId) ?? notes[0]
  // Diagnoses are scoped to the active note's visit so the list matches
  // ChiroTouch's per-note DX panel exactly (it changes as you flip notes).
  const diagnoses = currentNote?.diagnoses ?? []

  return (
    <SectionCard
      title="Notes & diagnoses"
      caption="The diagnoses on the left are the DX set for the selected note's visit — mirroring ChiroTouch's chart-note view."
    >
      <Box sx={{ display: 'flex', gap: 2, alignItems: 'flex-start' }}>
        {/* Left rail: persistent patient diagnoses. */}
        <Box sx={{ width: 280, flexShrink: 0 }}>
          <Typography
            variant="overline"
            color="text.secondary"
            sx={{ display: 'block', mb: 1 }}
          >
            Visit diagnoses ({diagnoses.length})
          </Typography>
          {diagnoses.length === 0 ? (
            <Typography variant="body2" color="text.secondary">
              No diagnoses recorded for this visit.
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
                {notes.map((n) => {
                  // Clinical date — server emits as "yyyy-MM-dd" so the
                  // formatter renders the calendar day directly (no EDT
                  // midnight-UTC shift). Falls back to "Note N" when missing.
                  const dateStr = formatShortDate(n.noteDate) ?? `Note ${Math.abs(n.id)}`
                  const verdict = n.latestScrub?.verdict
                  const verdictDot = verdict ? (
                    <Box
                      sx={{
                        width: 8,
                        height: 8,
                        borderRadius: '50%',
                        bgcolor:
                          verdict === 'pass'
                            ? 'success.main'
                            : verdict === 'fail'
                            ? 'error.main'
                            : 'warning.main',
                      }}
                    />
                  ) : null
                  const label = (
                    <Box sx={{ display: 'inline-flex', alignItems: 'center', gap: 0.75 }}>
                      {verdictDot}
                      <span>{dateStr}</span>
                      {n.source === 'portal' && (
                        <Chip
                          size="small"
                          color="primary"
                          variant="outlined"
                          label="Portal"
                          sx={{ height: 18, '& .MuiChip-label': { px: 0.75, fontSize: 10 } }}
                        />
                      )}
                    </Box>
                  )
                  return <Tab key={n.id} value={n.id} label={label} />
                })}
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
                      {currentNote.source === 'portal' ? (
                        <Chip
                          size="small"
                          color="primary"
                          variant="outlined"
                          label="Portal correction"
                        />
                      ) : (
                        <>
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
                        </>
                      )}
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
                      Note text not yet available for this row.
                    </Typography>
                  )}

                  {/* Per-note scrub. Each note is its own billable unit
                      (one HCFA claim line per provider visit). The latest
                      scrub is preloaded inline via currentNote.latestScrub
                      so the panel renders instantly without a follow-up
                      fetch. Re-scrub button POSTs to /notes/{id}/scrub. */}
                  {currentNote.doctorNoteId ? (
                    <ScrubPanel
                      doctorNoteId={currentNote.doctorNoteId}
                      initial={(currentNote.latestScrub as never) ?? null}
                    />
                  ) : (
                    <Typography variant="body2" color="text.secondary" sx={{ mt: 2 }}>
                      This note hasn't been synced into our DB yet — scrub will be available after the next sync cycle.
                    </Typography>
                  )}
                </Box>
              )}
            </>
          )}
        </Box>
      </Box>

      {/* Kickback only mounts for chart-sourced notes — portal corrections
          are themselves the response to a kickback, so re-kicking is a no-op. */}
      {currentNote && currentNote.source !== 'portal' && (
        <KickbackModal
          open={kickbackOpen}
          onClose={() => setKickbackOpen(false)}
          patientId={(record?.patientId as number) ?? 0}
          chartNoteId={currentNote.id}
          noteDateLabel={formatShortDate(currentNote.noteDate) ?? `Note ${currentNote.id}`}
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
