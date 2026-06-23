import { useRef, useState } from 'react'
import { useNotify } from 'react-admin'
import {
  Button,
  ButtonGroup,
  CircularProgress,
  ClickAwayListener,
  Grow,
  MenuItem,
  MenuList,
  Paper,
  Popper,
} from '@mui/material'
import ArrowDropDown from '@mui/icons-material/ArrowDropDown'
import Description from '@mui/icons-material/Description'

const WORKFLOW_API = import.meta.env.VITE_WORKFLOW_API_URL || '/workflow-api'

// HCFA generation has three output modes:
//   - Mail: data-only PDF for pre-printed red forms (default click).
//   - Fax preview: composites the blank form image, opens PDF for review.
//   - Fax now: same fax-mode PDF, sent directly to the carrier via Documo
//     using the fax number on the patient's InsPolicy.
// Mail/fax-preview both open the PDF in a new tab; "Fax now" never opens —
// just fires the send and shows a toast with the Documo job id.
type Props = {
  patientId: number
  visitId: number
}

const HcfaSplitButton = ({ patientId, visitId }: Props) => {
  const [open, setOpen] = useState(false)
  const [sending, setSending] = useState(false)
  const anchorRef = useRef<HTMLDivElement | null>(null)
  const notify = useNotify()

  const open_ = (mode: 'mail' | 'fax') => {
    const url = `${WORKFLOW_API}/hcfa/preview?patientId=${patientId}&appointmentId=${visitId}&mode=${mode}`
    window.open(url, '_blank')
  }

  const sendFax = async () => {
    setOpen(false)
    setSending(true)
    try {
      const resp = await fetch(
        `${WORKFLOW_API}/fax/hcfa?patientId=${patientId}&appointmentId=${visitId}`,
        { method: 'POST' },
      )
      const body = await resp.json().catch(() => ({}))
      if (!resp.ok) throw new Error(body?.error || `HTTP ${resp.status}`)
      const id = body?.results?.[0]?.faxId || '(no id)'
      const to = body?.results?.[0]?.to || ''
      notify(`Fax sent to ${to} (Documo id: ${id})`, { type: 'success' })
    } catch (e) {
      notify(`Fax failed: ${e instanceof Error ? e.message : 'unknown error'}`, {
        type: 'error',
      })
    } finally {
      setSending(false)
    }
  }

  return (
    <>
      <ButtonGroup size="small" variant="outlined" color="primary" ref={anchorRef}>
        <Button startIcon={<Description fontSize="small" />} onClick={() => open_('mail')}>
          Generate HCFA
        </Button>
        <Button
          size="small"
          aria-label="HCFA output mode"
          onClick={() => setOpen((o) => !o)}
          sx={{ minWidth: 0, px: 0.5 }}
          disabled={sending}
        >
          {sending ? <CircularProgress size={14} /> : <ArrowDropDown />}
        </Button>
      </ButtonGroup>
      <Popper
        open={open}
        anchorEl={anchorRef.current}
        placement="bottom-end"
        transition
        disablePortal
        sx={{ zIndex: 1300 }}
      >
        {({ TransitionProps }) => (
          <Grow {...TransitionProps}>
            <Paper elevation={3}>
              <ClickAwayListener onClickAway={() => setOpen(false)}>
                <MenuList dense autoFocusItem>
                  <MenuItem
                    onClick={() => {
                      setOpen(false)
                      open_('mail')
                    }}
                  >
                    Mail (pre-printed form, data only)
                  </MenuItem>
                  <MenuItem
                    onClick={() => {
                      setOpen(false)
                      open_('fax')
                    }}
                  >
                    Fax preview (overlay on blank form)
                  </MenuItem>
                  <MenuItem onClick={sendFax}>Fax now (Documo)</MenuItem>
                </MenuList>
              </ClickAwayListener>
            </Paper>
          </Grow>
        )}
      </Popper>
    </>
  )
}

export default HcfaSplitButton
