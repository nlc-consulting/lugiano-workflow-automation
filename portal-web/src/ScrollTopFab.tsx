import { useEffect, useState } from 'react'
import { Fab, Zoom } from '@mui/material'
import KeyboardArrowUp from '@mui/icons-material/KeyboardArrowUp'

// Floating "jump to top" button. Appears once the page is scrolled down and
// stays pinned bottom-right as you scroll. Robust to whichever element actually
// scrolls — react-admin can scroll either the window or its content pane
// (.RaLayout-content) depending on layout — so we watch both.
const THRESHOLD = 240

const ScrollTopFab = () => {
  const [show, setShow] = useState(false)

  useEffect(() => {
    const content = document.querySelector('.RaLayout-content') as HTMLElement | null
    const check = () => {
      const winY = window.scrollY || document.documentElement.scrollTop || 0
      const contentY = content?.scrollTop ?? 0
      setShow(Math.max(winY, contentY) > THRESHOLD)
    }
    check()
    window.addEventListener('scroll', check, { passive: true })
    content?.addEventListener('scroll', check, { passive: true })
    return () => {
      window.removeEventListener('scroll', check)
      content?.removeEventListener('scroll', check)
    }
  }, [])

  const toTop = () => {
    window.scrollTo({ top: 0, behavior: 'smooth' })
    document
      .querySelector('.RaLayout-content')
      ?.scrollTo?.({ top: 0, behavior: 'smooth' })
  }

  return (
    <Zoom in={show}>
      <Fab
        color="primary"
        size="medium"
        aria-label="Scroll to top"
        onClick={toTop}
        sx={{
          position: 'fixed',
          bottom: 24,
          right: 24,
          zIndex: (t) => t.zIndex.tooltip,
        }}
      >
        <KeyboardArrowUp />
      </Fab>
    </Zoom>
  )
}

export default ScrollTopFab
