import {
  BooleanField,
  Datagrid,
  FunctionField,
  List,
  TextField,
  type RaRecord,
} from 'react-admin'
import VerifyPipButton from './VerifyPipButton'
import EstDateTimeField from './EstDateTimeField'

type CaseRecord = RaRecord & {
  firstName?: string
  lastName?: string
}

const CaseList = () => (
  <List title="Patients in Workflow" sort={{ field: 'lastUpdatedAt', order: 'DESC' }}>
    <Datagrid rowClick="show" bulkActionButtons={false}>
      <TextField source="patientId" label="Patient ID" />
      <FunctionField
        label="Patient"
        render={(r: CaseRecord) => `${r.firstName ?? ''} ${r.lastName ?? ''}`.trim()}
      />
      <BooleanField source="insuranceProvided" label="Insurance" />
      <BooleanField source="doctorNotesReceived" label="Doctor Notes" />
      <BooleanField source="pipVerified" label="PIP Verified" />
      <TextField source="currentState" label="State" />
      <EstDateTimeField source="addedAt" label="Added" />
      <EstDateTimeField source="lastUpdatedAt" label="Updated" />
      <VerifyPipButton />
    </Datagrid>
  </List>
)

export default CaseList
