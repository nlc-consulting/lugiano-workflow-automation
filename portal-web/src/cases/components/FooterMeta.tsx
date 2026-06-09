import { useRecordContext } from 'react-admin'
import { Typography } from '@mui/material'
import { formatStamp } from './formatters'

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

export default FooterMeta
