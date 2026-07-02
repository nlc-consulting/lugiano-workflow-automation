// Date/time formatting shared across the case-detail components.
// Server emits clinical dates as "YYYY-MM-DD" (timezone-free) and full ISO
// strings for system timestamps; format each correctly so the EST/EDT shift
// doesn't move a calendar date to the previous day.

const isDateOnly = (s: string) => /^\d{4}-\d{2}-\d{2}$/.test(s)

export const formatShortDate = (s?: string | null): string | null => {
  if (!s) return null
  if (isDateOnly(s)) {
    const [y, m, d] = s.split('-').map(Number)
    return new Date(y, m - 1, d).toLocaleDateString('en-US')
  }
  return new Date(s).toLocaleDateString('en-US', { timeZone: 'America/New_York' })
}

export const formatStamp = (s?: string | null): string =>
  s
    ? new Date(s).toLocaleString('en-US', {
        timeZone: 'America/New_York',
        dateStyle: 'short',
        timeStyle: 'short',
      }) + ' EDT'
    : '—'

// Note clinical date + time, as stored (clinic-local wall clock). The server
// emits "yyyy-MM-dd HH:mm:ss"; build a local Date from the parts so the exact
// wall-clock renders without any timezone conversion. Falls back to date-only
// for the legacy "yyyy-MM-dd" shape.
export const formatNoteStamp = (s?: string | null): string => {
  if (!s) return '—'
  const m = s.match(/^(\d{4})-(\d{2})-(\d{2})[ T](\d{2}):(\d{2})/)
  if (m) {
    const d = new Date(+m[1], +m[2] - 1, +m[3], +m[4], +m[5])
    return d.toLocaleString('en-US', {
      year: 'numeric',
      month: 'numeric',
      day: 'numeric',
      hour: 'numeric',
      minute: '2-digit',
    })
  }
  return formatShortDate(s) ?? '—'
}

export const formatVisitTime = (iso?: string | null) =>
  iso
    ? new Date(iso).toLocaleTimeString('en-US', {
        timeZone: 'America/New_York',
        hour: 'numeric',
        minute: '2-digit',
      })
    : ''
