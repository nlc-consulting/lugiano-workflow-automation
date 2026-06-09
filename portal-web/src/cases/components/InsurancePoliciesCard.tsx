import { ArrayField, Datagrid, DateField, TextField } from 'react-admin'
import { Typography } from '@mui/material'
import SectionCard from './SectionCard'

const InsurancePoliciesCard = () => (
  <SectionCard title="Insurance policies">
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
  </SectionCard>
)

export default InsurancePoliciesCard
