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

export const formatVisitTime = (iso?: string | null) =>
  iso
    ? new Date(iso).toLocaleTimeString('en-US', {
        timeZone: 'America/New_York',
        hour: 'numeric',
        minute: '2-digit',
      })
    : ''
