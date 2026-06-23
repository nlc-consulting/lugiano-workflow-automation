import { useMemo, useState } from 'react'
import { useRecordContext, type RaRecord } from 'react-admin'
import {
  Box,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TablePagination,
  TableRow,
  Typography,
} from '@mui/material'
import SectionCard from './SectionCard'
import { formatShortDate } from './formatters'

type ChargeRow = {
  date?: string | null
  code?: string | null
  description?: string | null
  modifier1?: string | null
  modifier2?: string | null
  diagnoses?: string | null
  amount?: number | null
}

const formatAmount = (n?: number | null) =>
  typeof n === 'number'
    ? n.toLocaleString('en-US', { style: 'currency', currency: 'USD' })
    : '—'

const ChargesCard = () => {
  const r = useRecordContext<RaRecord & { charges?: ChargeRow[] }>()
  const charges = r?.charges ?? []
  const total = charges.reduce(
    (sum, c) => sum + (typeof c.amount === 'number' ? c.amount : 0),
    0,
  )

  // In-memory pagination — some patients' ledgers run to hundreds of lines and
  // rendering them all at once both lags the page and makes the rest of the
  // card stack scroll past. Defaults to 10/page; user can bump up to 100.
  const [page, setPage] = useState(0)
  const [rowsPerPage, setRowsPerPage] = useState(10)

  const visible = useMemo(
    () => charges.slice(page * rowsPerPage, (page + 1) * rowsPerPage),
    [charges, page, rowsPerPage],
  )

  return (
    <SectionCard
      title="Ledger"
      caption="All charges for the visits the notes below belong to. Expand a note row to review the bill alongside that specific note."
      trailing={
        charges.length > 0 ? (
          <Typography variant="subtitle1" sx={{ fontWeight: 600 }}>
            Total: {formatAmount(total)}
          </Typography>
        ) : null
      }
    >
      {charges.length === 0 ? (
        <Typography variant="body2" color="text.secondary" sx={{ p: 2 }}>
          No charges entered for the visits matched to recent notes.
        </Typography>
      ) : (
        <Box sx={{ overflowX: 'auto' }}>
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>Date</TableCell>
                <TableCell>Code</TableCell>
                <TableCell>Description</TableCell>
                <TableCell>M1</TableCell>
                <TableCell>M2</TableCell>
                <TableCell>Diagnoses</TableCell>
                <TableCell align="right">Amount</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {visible.map((c, i) => (
                <TableRow key={i} hover>
                  <TableCell>{formatShortDate(c.date) ?? '—'}</TableCell>
                  <TableCell>{c.code ?? '—'}</TableCell>
                  <TableCell>{c.description ?? '—'}</TableCell>
                  <TableCell>{c.modifier1 ?? ''}</TableCell>
                  <TableCell>{c.modifier2 ?? ''}</TableCell>
                  <TableCell>{c.diagnoses ?? ''}</TableCell>
                  <TableCell align="right">{formatAmount(c.amount)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
          <TablePagination
            component="div"
            count={charges.length}
            page={page}
            onPageChange={(_, p) => setPage(p)}
            rowsPerPage={rowsPerPage}
            rowsPerPageOptions={[10, 25, 50, 100]}
            onRowsPerPageChange={(e) => {
              setRowsPerPage(parseInt(e.target.value, 10))
              setPage(0)
            }}
          />
        </Box>
      )}
    </SectionCard>
  )
}

export default ChargesCard
