import { ArrayField, Datagrid, DateField, TextField } from 'react-admin'
import { Box, Typography } from '@mui/material'
import SectionCard from './SectionCard'

const InsurancePoliciesCard = () => (
  <SectionCard title="Insurance policies">
    <Box sx={{ overflowX: 'auto' }}>
      <ArrayField source="policies">
        <Datagrid
          bulkActionButtons={false}
          rowClick={false}
          empty={
            <Typography variant="body2" color="text.secondary" sx={{ p: 2 }}>
              No insurance policies on file.
            </Typography>
          }
        >
          <TextField source="insurer" label="Insurer" />
          <TextField source="coverageType" label="Coverage" />
          <DateField source="effectiveDate" label="Effective" />
          <DateField source="terminationDate" label="Termination" />
        </Datagrid>
      </ArrayField>
    </Box>
  </SectionCard>
)

export default InsurancePoliciesCard
