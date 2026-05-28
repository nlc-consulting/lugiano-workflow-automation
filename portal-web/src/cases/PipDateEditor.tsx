import { useState } from 'react'
import { useNotify, useRecordContext, useRefresh, type RaRecord } from 'react-admin'
import { Box, Button, TextField as MuiTextField, Typography } from '@mui/material'

const WORKFLOW_API = import.meta.env.VITE_WORKFLOW_API_URL || '/workflow-api'

type CaseRecord = RaRecord & { pipVerified?: boolean; pipVerifiedAt?: string }

const toDateInput = (iso?: string) => (iso ? iso.slice(0, 10) : '')
const today = () => new Date().toISOString().slice(0, 10)

// Verify PIP, or edit the verified date later (rep records the actual approval date).
const PipDateEditor = () => {
  const record = useRecordContext<CaseRecord>()
  const notify = useNotify()
  const refresh = useRefresh()
  const [date, setDate] = useState(toDateInput(record?.pipVerifiedAt) || today())
  const [saving, setSaving] = useState(false)

  if (!record) return null

  const save = async () => {
    setSaving(true)
    try {
      const res = await fetch(
        `${WORKFLOW_API}/cases/${record.id}/verify-pip?date=${encodeURIComponent(date)}`,
        { method: 'POST' },
      )
      if (!res.ok) throw new Error(`HTTP ${res.status}`)
      notify(record.pipVerified ? 'PIP date updated' : 'PIP verified', { type: 'success' })
      refresh()
    } catch {
      notify('Could not save PIP date', { type: 'error' })
    } finally {
      setSaving(false)
    }
  }

  return (
    <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, my: 1 }}>
      <Typography variant="body2">PIP verified date:</Typography>
      <MuiTextField
        type="date"
        size="small"
        value={date}
        onChange={(e) => setDate(e.target.value)}
      />
      <Button variant="contained" size="small" onClick={save} disabled={saving}>
        {record.pipVerified ? 'Update PIP date' : 'Verify PIP'}
      </Button>
    </Box>
  )
}

export default PipDateEditor
