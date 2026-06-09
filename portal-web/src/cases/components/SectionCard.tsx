import type { ReactNode } from 'react'
import { Box, Card, CardContent, Typography } from '@mui/material'

// Generic titled card with optional caption and a trailing slot (used for totals,
// counters, etc.). Shared by the policies, ledger, and notes-and-diagnoses cards.
type Props = {
  title: string
  caption?: string
  trailing?: ReactNode
  children: ReactNode
}

const SectionCard = ({ title, caption, trailing, children }: Props) => (
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

export default SectionCard
