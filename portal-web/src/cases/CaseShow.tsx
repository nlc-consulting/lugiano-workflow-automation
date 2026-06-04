import { useState } from 'react'
import {
  ArrayField,
  Datagrid,
  DateField,
  FunctionField,
  ListButton,
  Show,
  TextField,
  TopToolbar,
  useNotify,
  useRecordContext,
  useRefresh,
  useShowContext,
  type RaRecord,
} from 'react-admin'
import {
  Alert,
  Box,
  Button,
  Card,
  CardContent,
  Chip,
  Tab,
  Tabs,
  Tooltip,
  Typography,
} from '@mui/material'
import ArrowBack from '@mui/icons-material/ArrowBack'
import CheckCircle from '@mui/icons-material/CheckCircle'
import RadioButtonUnchecked from '@mui/icons-material/RadioButtonUnchecked'
import ReplyAll from '@mui/icons-material/ReplyAll'
import PipDateEditor from './PipDateEditor'
import KickbackModal from './KickbackModal'
import ScrubPanel from './ScrubPanel'

// ---------------------------------------------------------------------------
// Visit-match chip on each note (unchanged behavior, extracted helpers).
// ---------------------------------------------------------------------------

type NoteVisit = {
  visitId?: number | null
  visitTime?: string | null
  visitDoctor?: string | null
  visitsSameDay?: number | null
  matchScore?: number | null
  matchReasons?: string[] | null
}

const formatVisitTime = (iso?: string | null) =>
  iso
    ? new Date(iso).toLocaleTimeString('en-US', {
        timeZone: 'America/New_York',
        hour: 'numeric',
        minute: '2-digit',
      })
    : ''

const scoreColor = (score: number): 'success' | 'warning' | 'error' =>
  score >= 85 ? 'success' : score >= 50 ? 'warning' : 'error'

// Render the visit-match chip from a note object directly (not via record
// context) so it can live anywhere — inside the tab metadata header, hover
// tooltip, etc. The chip color and tooltip explain why a match is uncertain.
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

// ---------------------------------------------------------------------------
// Date helpers. Server emits clinical dates as "YYYY-MM-DD" (timezone-free)
// and full ISO strings for system timestamps; format each correctly so the
// EST/EDT timezone shift doesn't move a calendar date to the previous day.
// ---------------------------------------------------------------------------

const isDateOnly = (s: string) => /^\d{4}-\d{2}-\d{2}$/.test(s)

const formatShortDate = (s?: string | null): string | null => {
  if (!s) return null
  if (isDateOnly(s)) {
    const [y, m, d] = s.split('-').map(Number)
    return new Date(y, m - 1, d).toLocaleDateString('en-US')
  }
  return new Date(s).toLocaleDateString('en-US', { timeZone: 'America/New_York' })
}

const formatStamp = (s?: string | null): string =>
  s
    ? new Date(s).toLocaleString('en-US', {
        timeZone: 'America/New_York',
        dateStyle: 'short',
        timeStyle: 'short',
      }) + ' EDT'
    : '—'

// ---------------------------------------------------------------------------
// Workflow state → readable label + color band. Single place to tune when
// new states land (scrub-related ones in the next epic).
// ---------------------------------------------------------------------------

const stateColor = (state?: string): 'error' | 'warning' | 'success' | 'default' => {
  switch (state) {
    case 'AwaitingInsurance':
      return 'error'
    case 'AwaitingPipVerification':
    case 'AwaitingDoctorNotes':
    case 'AwaitingDoctorCorrection':
      return 'warning'
    case 'ReadyForAiScrubbing':
      return 'success'
    default:
      return 'default'
  }
}

const stateLabel = (state?: string): string => {
  switch (state) {
    case 'AwaitingInsurance':
      return 'Awaiting insurance'
    case 'AwaitingPipVerification':
      return 'Awaiting PIP verification'
    case 'AwaitingDoctorNotes':
      return 'Awaiting doctor notes'
    case 'AwaitingDoctorCorrection':
      return 'Awaiting doctor correction'
    case 'ReadyForAiScrubbing':
      return 'Ready for AI scrubbing'
    default:
      return state ?? '—'
  }
}

// ---------------------------------------------------------------------------
// Page sections. Each is its own card so visual hierarchy is obvious and we
// can rearrange / extend (scrub findings panel lands here later).
// ---------------------------------------------------------------------------

const PatientHeader = () => {
  const r = useRecordContext()
  if (!r) return null
  const name = [r.firstName, r.middleName, r.lastName].filter(Boolean).join(' ')
  const address = [r.address, r.city, r.state, r.zip].filter(Boolean).join(', ')
  return (
    <Card>
      <CardContent>
        <Box
          sx={{
            display: 'flex',
            justifyContent: 'space-between',
            alignItems: 'flex-start',
            gap: 2,
          }}
        >
          <Box>
            <Typography variant="h5" sx={{ fontWeight: 600 }}>
              {name || `Patient ${r.patientId}`}
            </Typography>
            <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
              ID {r.patientId} · {r.sex ?? '—'} · {r.primaryDoctor ?? '—'}
            </Typography>
            {address && (
              <Typography variant="body2" color="text.secondary">
                {address}
              </Typography>
            )}
          </Box>
          <Chip
            color={stateColor(r.currentState)}
            label={stateLabel(r.currentState)}
            sx={{ fontWeight: 600 }}
          />
        </Box>
      </CardContent>
    </Card>
  )
}

type StatusPillProps = {
  label: string
  present: boolean
  secondary?: string | null
}

const StatusPill = ({ label, present, secondary }: StatusPillProps) => (
  <Chip
    color={present ? 'success' : 'default'}
    variant={present ? 'filled' : 'outlined'}
    icon={present ? <CheckCircle /> : <RadioButtonUnchecked />}
    label={
      <Box component="span" sx={{ display: 'inline-flex', alignItems: 'baseline', gap: 0.75 }}>
        <Box component="span" sx={{ fontWeight: 600 }}>
          {label}
        </Box>
        {secondary && (
          <Box component="span" sx={{ opacity: 0.85, fontSize: '0.85em' }}>
            · {secondary}
          </Box>
        )}
      </Box>
    }
  />
)

const StatusRow = () => {
  const r = useRecordContext()
  if (!r) return null
  return (
    <Card>
      <CardContent>
        <Typography variant="overline" color="text.secondary" sx={{ display: 'block', mb: 1 }}>
          Billing readiness
        </Typography>
        <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1.5, alignItems: 'center' }}>
          <StatusPill
            label="Insurance"
            present={!!r.insuranceProvided}
            secondary={
              r.insuranceAddedAt ? `effective ${formatShortDate(r.insuranceAddedAt)}` : 'no policy on file'
            }
          />
          <StatusPill
            label="Doctor notes"
            present={!!r.doctorNotesReceived}
            secondary={
              r.doctorNotesReceivedAt ? `latest ${formatShortDate(r.doctorNotesReceivedAt)}` : 'none yet'
            }
          />
          <StatusPill
            label="PIP verified"
            present={!!r.pipVerified}
            secondary={
              r.pipVerifiedAt ? `on ${formatShortDate(r.pipVerifiedAt)}` : 'not verified'
            }
          />
        </Box>
        <Box sx={{ mt: 2 }}>
          <PipDateEditor />
        </Box>
      </CardContent>
    </Card>
  )
}

const SectionCard = ({
  title,
  caption,
  trailing,
  children,
}: {
  title: string
  caption?: string
  trailing?: React.ReactNode
  children: React.ReactNode
}) => (
  <Card>
    <CardContent>
      <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline', mb: 1 }}>
        <Box>
          <Typography variant="h6">{title}</Typography>
          {caption && (
            <Typography variant="caption" color="text.secondary">
              {caption}
            </Typography>
          )}
        </Box>
        {trailing}
      </Box>
      {children}
    </CardContent>
  </Card>
)

const InsurancePoliciesCard = () => (
  <SectionCard title="Insurance policies">
    <ArrayField source="policies">
      <Datagrid
        bulkActionButtons={false}
        rowClick={false}
        empty={
          <Typography variant="body2" color="text.secondary" sx={{ p: 2 }}>
            No insurance policies on file.
          </Typography>
        }
      >
        <TextField source="insurer" label="Insurer" />
        <TextField source="coverageType" label="Coverage" />
        <DateField source="effectiveDate" label="Effective" />
        <DateField source="terminationDate" label="Termination" />
      </Datagrid>
    </ArrayField>
  </SectionCard>
)

const ChargesCard = () => {
  const r = useRecordContext<RaRecord & { charges?: Array<{ amount?: number }> }>()
  const charges = r?.charges ?? []
  const total = charges.reduce((sum, c) => sum + (typeof c.amount === 'number' ? c.amount : 0), 0)
  const totalLabel = total.toLocaleString('en-US', { style: 'currency', currency: 'USD' })

  return (
    <SectionCard
      title="Ledger"
      caption="All charges for the visits the notes below belong to. Expand a note row to review the bill alongside that specific note."
      trailing={
        charges.length > 0 ? (
          <Typography variant="subtitle1" sx={{ fontWeight: 600 }}>
            Total: {totalLabel}
          </Typography>
        ) : null
      }
    >
      <ArrayField source="charges">
        <Datagrid
          bulkActionButtons={false}
          rowClick={false}
          empty={
            <Typography variant="body2" color="text.secondary" sx={{ p: 2 }}>
              No charges entered for the visits matched to recent notes.
            </Typography>
          }
        >
          <DateField source="date" label="Date" />
          <TextField source="code" label="Code" />
          <TextField source="description" label="Description" />
          <TextField source="modifier1" label="M1" />
          <TextField source="modifier2" label="M2" />
          <TextField source="diagnoses" label="Diagnoses" />
          <FunctionField
            label="Amount"
            render={(row: RaRecord) =>
              typeof row.amount === 'number'
                ? row.amount.toLocaleString('en-US', { style: 'currency', currency: 'USD' })
                : '—'
            }
          />
        </Datagrid>
      </ArrayField>
    </SectionCard>
  )
}

// Server extracts ICD-10 codes from the chart-note plain text (ChiroTouch embeds
// them in the note RTF, not in a structured table) and pairs them with catalog
// descriptions so each row reads "M54.12 · Radiculopathy, cervical reg".
type DiagnosisItem = { code: string; description?: string | null }

type NoteRow = NoteVisit & {
  id: number
  noteDate?: string | null
  doctor?: string | null
  plainText?: string | null
}

// Combined notes + diagnoses card. Mirrors ChiroTouch's chart-note view: the
// patient's diagnosis list stays pinned on the left while notes are reviewed
// as horizontal tabs on the right (one tab per loaded note). Switching tabs
// changes the note content; diagnoses don't move. Replaces the prior vertical
// notes list and the per-note expand-with-charges sidebar — full charge detail
// now lives in the Ledger card below.
const NotesAndDiagnosesCard = () => {
  const { record } = useShowContext<
    RaRecord & { id: number; patientId: number; notes?: NoteRow[]; diagnoses?: DiagnosisItem[] }
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
                  sx={{
                    py: 0.75,
                    borderBottom: '1px solid',
                    borderColor: 'divider',
                  }}
                >
                  <Typography
                    variant="body2"
                    sx={{ fontFamily: 'monospace', fontWeight: 700 }}
                  >
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
                      caps RTF reconstruction at the 3 most recent notes for
                      performance).
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

const FooterMeta = () => {
  const r = useRecordContext()
  if (!r) return null
  return (
    <Typography
      variant="caption"
      color="text.secondary"
      sx={{ display: 'block', textAlign: 'right', mt: 1 }}
    >
      Added to portal {formatStamp(r.addedAt)} · last updated {formatStamp(r.lastUpdatedAt)}
    </Typography>
  )
}

// Banner that surfaces when the case is awaiting a doctor correction.
// Appears between the workflow header and the rest so reviewers see immediately
// that this case is "out for fix" — not actionable until the doctor responds.
const AwaitingCorrectionBanner = () => {
  const r = useRecordContext()
  if (r?.currentState !== 'AwaitingDoctorCorrection') return null
  return (
    <Alert severity="warning" variant="outlined" icon={<ReplyAll fontSize="small" />}>
      This case is awaiting a doctor correction. It will auto-resume when the
      doctor adds a new chart note in ChiroTouch.
    </Alert>
  )
}

const CaseShowContent = () => (
  <Box sx={{ display: 'flex', flexDirection: 'column', gap: 2, my: 1 }}>
    <PatientHeader />
    <AwaitingCorrectionBanner />
    <StatusRow />
    <InsurancePoliciesCard />
    <NotesAndDiagnosesCard />
    {/* Ledger is the canonical bill view across all matched visits. Once the
        scrubbing UI lands, this whole stack is a good candidate for a top-level
        Tabs layout (Summary / Notes / Ledger / Scrubbing). */}
    <ChargesCard />
    <FooterMeta />
  </Box>
)

// Quick way back to the top of the patient list.
const ShowActions = () => (
  <TopToolbar>
    <ListButton label="Back to patients" icon={<ArrowBack />} />
  </TopToolbar>
)

const CaseShow = () => (
  <Show title="Patient detail" actions={<ShowActions />}>
    <CaseShowContent />
  </Show>
)

export default CaseShow
