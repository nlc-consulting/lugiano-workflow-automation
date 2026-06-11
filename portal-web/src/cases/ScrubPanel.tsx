import { useEffect, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  Chip,
  CircularProgress,
  Stack,
  Typography,
} from '@mui/material'
import AutoAwesome from '@mui/icons-material/AutoAwesome'
import CheckCircle from '@mui/icons-material/CheckCircle'
import ErrorIcon from '@mui/icons-material/Error'
import Warning from '@mui/icons-material/Warning'

const WORKFLOW_API = import.meta.env.VITE_WORKFLOW_API_URL || '/workflow-api'

type Section = { present: boolean; notes?: string | null }
type AssessmentSection = Section & { in_my_opinion_present?: boolean }
type AlignmentIssue = { code?: string | null; concern?: string | null }
type Alignment = { score?: number; issues?: AlignmentIssue[] }
type Issue = { severity: 'high' | 'medium' | 'low'; category?: string | null; description?: string | null }

type Findings = {
  verdict: 'pass' | 'needs_review' | 'fail'
  overall_confidence?: number
  summary?: string | null
  sections?: {
    subjective?: Section
    objective?: Section
    assessment?: AssessmentSection
    treatment_plan?: Section
    primary_treatment?: Section
  }
  diagnosis_alignment?: Alignment
  charge_alignment?: Alignment
  issues?: Issue[]
}

type ScrubResult = {
  id: number
  verdict: Findings['verdict']
  overallConfidence: number
  summary?: string | null
  findings: Findings
  modelUsed: string
  promptVersion: string
  ranAt: string
}

type Props = {
  // The note being scrubbed. doctorNoteId is our internal PK on DoctorNote;
  // both chart and portal-authored notes have one. All scrub endpoints are
  // keyed by it.
  doctorNoteId: number
  // Optional initial result preloaded with the case detail (avoids a refetch
  // on mount when the parent already has the latest). The panel will still
  // POST a fresh scrub on Re-scrub click.
  initial?: ScrubResult | null
}

const verdictColor = (v?: string): 'success' | 'warning' | 'error' | 'default' => {
  switch (v) {
    case 'pass': return 'success'
    case 'needs_review': return 'warning'
    case 'fail': return 'error'
    default: return 'default'
  }
}

const verdictLabel = (v?: string) => {
  switch (v) {
    case 'pass': return 'Pass'
    case 'needs_review': return 'Needs review'
    case 'fail': return 'Fail'
    default: return v ?? '—'
  }
}

const severityColor = (s: Issue['severity']): 'error' | 'warning' | 'inherit' => {
  switch (s) {
    case 'high': return 'error'
    case 'medium': return 'warning'
    default: return 'inherit'
  }
}

const SectionRow = ({
  label,
  section,
  warnNoInMyOpinion,
}: {
  label: string
  section?: Section | AssessmentSection
  warnNoInMyOpinion?: boolean
}) => {
  if (!section) return null
  const missingInMyOpinion =
    warnNoInMyOpinion && section.present && (section as AssessmentSection).in_my_opinion_present === false
  const ok = section.present && !missingInMyOpinion
  return (
    <Box sx={{ display: 'flex', alignItems: 'flex-start', gap: 1, py: 0.5 }}>
      {ok ? (
        <CheckCircle fontSize="small" color="success" />
      ) : (
        <Warning fontSize="small" color="warning" />
      )}
      <Box sx={{ minWidth: 0 }}>
        <Typography variant="body2" sx={{ fontWeight: 600 }}>
          {label}
          {missingInMyOpinion && (
            <Typography component="span" variant="caption" color="warning.main" sx={{ ml: 1 }}>
              missing "in my opinion"
            </Typography>
          )}
          {!section.present && (
            <Typography component="span" variant="caption" color="warning.main" sx={{ ml: 1 }}>
              missing
            </Typography>
          )}
        </Typography>
        {section.notes && (
          <Typography variant="caption" color="text.secondary">
            {section.notes}
          </Typography>
        )}
      </Box>
    </Box>
  )
}

const ScrubPanel = ({ doctorNoteId, initial = null }: Props) => {
  const [latest, setLatest] = useState<ScrubResult | null>(initial)
  // Skip the network round-trip when the parent preloaded — /cases/{id} now
  // embeds each note's latestScrub inline, so the panel renders instantly.
  const [loading, setLoading] = useState(initial == null)
  const [scrubbing, setScrubbing] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (initial !== null) {
      setLatest(initial)
      setLoading(false)
      return
    }
    setLatest(null)
    setError(null)
    setLoading(true)
    fetch(`${WORKFLOW_API}/notes/${doctorNoteId}/scrub`)
      .then(async (r) => {
        if (r.status === 204) return null
        if (!r.ok) throw new Error(`HTTP ${r.status}`)
        return r.json() as Promise<ScrubResult>
      })
      .then(setLatest)
      .catch((e) => setError(e instanceof Error ? e.message : 'Failed to load scrub result.'))
      .finally(() => setLoading(false))
  }, [doctorNoteId, initial])

  const runScrub = async () => {
    setScrubbing(true)
    setError(null)
    try {
      const res = await fetch(`${WORKFLOW_API}/notes/${doctorNoteId}/scrub`, { method: 'POST' })
      if (!res.ok) {
        const body = await res.json().catch(() => ({}))
        throw new Error(body.error ?? `HTTP ${res.status}`)
      }
      setLatest((await res.json()) as ScrubResult)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Scrub failed.')
    } finally {
      setScrubbing(false)
    }
  }

  const findings = latest?.findings
  const sections = findings?.sections

  return (
    <Box
      sx={{
        mt: 2,
        p: 1.5,
        border: '1px solid',
        borderColor: 'divider',
        borderRadius: 1,
        bgcolor: 'background.paper',
      }}
    >
      <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', mb: 1 }}>
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
          <AutoAwesome fontSize="small" color="primary" />
          <Typography variant="overline" color="text.secondary">
            AI scrub
          </Typography>
          {latest && (
            <Chip
              size="small"
              color={verdictColor(latest.verdict)}
              label={verdictLabel(latest.verdict)}
              sx={{ fontWeight: 600 }}
            />
          )}
          {latest && (
            <Typography variant="caption" color="text.secondary">
              {/* Confidence is hidden from the header — it reads like a quality
                  score and confuses reviewers (a high-confidence Fail looks
                  "good"). Still persisted on ScrubResult.OverallConfidence for
                  calibration work (task #25). */}
              {new Date(latest.ranAt).toLocaleString('en-US', { timeZone: 'America/New_York' })}
            </Typography>
          )}
        </Box>
        <Button
          size="small"
          variant="outlined"
          onClick={runScrub}
          disabled={scrubbing}
          startIcon={scrubbing ? <CircularProgress size={14} /> : <AutoAwesome fontSize="small" />}
        >
          {scrubbing ? 'Scrubbing…' : latest ? 'Re-scrub note' : 'Scrub note'}
        </Button>
      </Box>

      {error && (
        <Alert severity="error" sx={{ mb: 1 }}>
          {error}
        </Alert>
      )}

      {loading && !latest && (
        <Typography variant="body2" color="text.secondary">Loading prior scrub…</Typography>
      )}

      {!loading && !latest && !error && !scrubbing && (
        <Typography variant="body2" color="text.secondary">
          No scrub yet. Click "Scrub note" to evaluate this note against its visit's diagnoses.
        </Typography>
      )}

      {findings && (
        <Stack spacing={1}>
          {findings.summary && (
            <Typography variant="body2">{findings.summary}</Typography>
          )}

          <Box>
            <SectionRow label="Subjective" section={sections?.subjective} />
            <SectionRow label="Objective" section={sections?.objective} />
            <SectionRow label="Assessment" section={sections?.assessment} warnNoInMyOpinion />
            <SectionRow label="Treatment plan" section={sections?.treatment_plan} />
            <SectionRow label="Primary treatment" section={sections?.primary_treatment} />
          </Box>

          {(() => {
            // Hide chips that have no actual issues. The model still fills
            // charge_alignment.score even when we tell it not to evaluate
            // charges (prompt v6+), so we suppress the chip unless there's
            // something to actually look at.
            const dxHasIssues = (findings.diagnosis_alignment?.issues?.length ?? 0) > 0
            const chargeHasIssues = (findings.charge_alignment?.issues?.length ?? 0) > 0
            if (!dxHasIssues && !chargeHasIssues) return null
            return (
              <Box sx={{ display: 'flex', gap: 2, flexWrap: 'wrap' }}>
                {dxHasIssues && (
                  <Chip
                    size="small"
                    variant="outlined"
                    label={`Diagnosis alignment: ${findings.diagnosis_alignment?.score ?? '—'}`}
                  />
                )}
                {chargeHasIssues && (
                  <Chip
                    size="small"
                    variant="outlined"
                    label={`Charge alignment: ${findings.charge_alignment?.score ?? '—'}`}
                  />
                )}
              </Box>
            )
          })()}

          {findings.issues && findings.issues.length > 0 && (
            <Box>
              <Typography variant="overline" color="text.secondary">
                Issues
              </Typography>
              <Stack spacing={0.5}>
                {findings.issues.map((it, i) => (
                  <Box key={i} sx={{ display: 'flex', alignItems: 'flex-start', gap: 1 }}>
                    {it.severity === 'high' ? (
                      <ErrorIcon fontSize="small" color={severityColor(it.severity)} />
                    ) : (
                      <Warning fontSize="small" color={severityColor(it.severity)} />
                    )}
                    <Box>
                      <Typography variant="caption" sx={{ fontWeight: 600, textTransform: 'uppercase' }}>
                        {it.severity}
                        {it.category && ` · ${it.category}`}
                      </Typography>
                      <Typography variant="body2">{it.description}</Typography>
                    </Box>
                  </Box>
                ))}
              </Stack>
            </Box>
          )}

          <Typography variant="caption" color="text.secondary">
            {latest?.modelUsed} · prompt {latest?.promptVersion}
          </Typography>
        </Stack>
      )}
    </Box>
  )
}

export default ScrubPanel
