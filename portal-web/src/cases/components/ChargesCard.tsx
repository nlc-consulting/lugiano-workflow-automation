import {
  ArrayField,
  Datagrid,
  DateField,
  FunctionField,
  TextField,
  useRecordContext,
  type RaRecord,
} from 'react-admin'
import { Box, Typography } from '@mui/material'
import SectionCard from './SectionCard'

const formatAmount = (n?: number) =>
  typeof n === 'number'
    ? n.toLocaleString('en-US', { style: 'currency', currency: 'USD' })
    : '—'

const ChargesCard = () => {
  const r = useRecordContext<RaRecord & { charges?: Array<{ amount?: number }> }>()
  const charges = r?.charges ?? []
  const total = charges.reduce((sum, c) => sum + (typeof c.amount === 'number' ? c.amount : 0), 0)

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
      <Box sx={{ overflowX: 'auto' }}>
        <ArrayField source="charges">
          <Datagrid
            bulkActionButtons={false}
            rowClick={false}
            empty={
              <Typography variant="body2" color="text.secondary" sx={{ p: 2 }}>
                No charges entered for the visits matched to recent notes.
              </Typography>
            }
          >
            <DateField source="date" label="Date" />
            <TextField source="code" label="Code" />
            <TextField source="description" label="Description" />
            <TextField source="modifier1" label="M1" />
            <TextField source="modifier2" label="M2" />
            <TextField source="diagnoses" label="Diagnoses" />
            <FunctionField
              label="Amount"
              render={(row: RaRecord) => formatAmount(row.amount)}
            />
          </Datagrid>
        </ArrayField>
      </Box>
    </SectionCard>
  )
}

export default ChargesCard
