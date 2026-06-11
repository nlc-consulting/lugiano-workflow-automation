import { useShowContext, type RaRecord } from 'react-admin'
import { Box, Chip, Typography } from '@mui/material'
import SectionCard from './SectionCard'

type CaseScrubSummary = {
  rolledVerdict?: 'pass' | 'needs_review' | 'fail' | null
  passCount?: number
  needsReviewCount?: number
  failCount?: number
  scrubbedNoteCount?: number
  latestRanAt?: string | null
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

// Case-level rollup view. The actual scrubs are per-note (one per provider
// per visit, matching how each note becomes one HCFA claim line). This card
// shows the worst-of verdict across all the patient's note scrubs and the
// pass / needs-review / fail counts. Each note tab below has its own
// ScrubPanel with full per-note findings + re-scrub button.
const CaseScrubCard = () => {
  const { record } = useShowContext<RaRecord & { caseScrubSummary?: CaseScrubSummary | null }>()
  const summary = record?.caseScrubSummary ?? null

  return (
    <SectionCard
      title="AI scrub summary"
      caption="Each chart note is scrubbed individually against its visit's diagnoses and charges — same unit that ships on a claim. Drill into a note tab below for per-note findings."
    >
      {!summary ? (
        <Typography variant="body2" color="text.secondary">
          No scrubs yet for this patient. Open a note tab and click "Scrub note" to evaluate.
        </Typography>
      ) : (
        <Box sx={{ display: 'flex', flexDirection: 'column', gap: 1 }}>
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, flexWrap: 'wrap' }}>
            {summary.rolledVerdict && (
              <Chip
                size="small"
                color={VERDICT_COLORS[summary.rolledVerdict] ?? 'default'}
                label={VERDICT_LABELS[summary.rolledVerdict] ?? summary.rolledVerdict}
                sx={{ fontWeight: 600 }}
              />
            )}
            {(summary.passCount ?? 0) > 0 && (
              <Chip size="small" variant="outlined" color="success" label={`${summary.passCount} pass`} />
            )}
            {(summary.needsReviewCount ?? 0) > 0 && (
              <Chip size="small" variant="outlined" color="warning" label={`${summary.needsReviewCount} needs review`} />
            )}
            {(summary.failCount ?? 0) > 0 && (
              <Chip size="small" variant="outlined" color="error" label={`${summary.failCount} fail`} />
            )}
            <Typography variant="caption" color="text.secondary">
              across {summary.scrubbedNoteCount} note{(summary.scrubbedNoteCount ?? 0) === 1 ? '' : 's'}
            </Typography>
          </Box>
          {summary.latestRanAt && (
            <Typography variant="caption" color="text.secondary">
              Latest run: {new Date(summary.latestRanAt).toLocaleString('en-US', {
                timeZone: 'America/New_York',
              })}
            </Typography>
          )}
        </Box>
      )}
    </SectionCard>
  )
}

export default CaseScrubCard
