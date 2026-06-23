import { useRef, useState } from 'react'
import {
  Button,
  ButtonGroup,
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

// HCFA generation has two real-world output modes: pre-printed red form
// going out via snail mail (data only), and fax to the carrier (data
// composited on top of a blank CMS-1500 image so the recipient sees a
// complete form). The coordinates are identical — CT uses one alignment
// for both — so the only difference is whether we paint the overlay.
//
// Split button: main click = mail (the historical default), dropdown
// exposes fax. Two clicks for fax, one for mail.
type Props = {
  patientId: number
  visitId: number
}

const HcfaSplitButton = ({ patientId, visitId }: Props) => {
  const [open, setOpen] = useState(false)
  const anchorRef = useRef<HTMLDivElement | null>(null)

  const open_ = (mode: 'mail' | 'fax') => {
    const url = `${WORKFLOW_API}/hcfa/preview?patientId=${patientId}&appointmentId=${visitId}&mode=${mode}`
    window.open(url, '_blank')
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
        >
          <ArrowDropDown />
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
                    Fax (overlay on blank form)
                  </MenuItem>
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
