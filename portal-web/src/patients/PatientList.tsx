import {
  BooleanInput,
  Datagrid,
  FunctionField,
  List,
  TextField,
  TextInput,
  type RaRecord,
} from 'react-admin'

type PatientRow = RaRecord & {
  firstName?: string
  lastName?: string
  accountNo?: string
}

// Active-patient search across the live PSChiro database. Clicks open the same
// case-detail view used by the automation tab — works for any PatientID because
// /cases/{id} pulls demographics/notes/charges live and treats workflow data as
// optional.
const PatientList = () => (
  <List
    title="Patients"
    sort={{ field: 'id', order: 'DESC' }}
    filters={[
      <TextInput
        key="q"
        source="q"
        label="Search name / account # / ID"
        alwaysOn
        resettable
      />,
      <BooleanInput key="includeInactive" source="includeInactive" label="Include inactive" />,
    ]}
    exporter={false}
  >
    <Datagrid
      rowClick={(id) => `/cases/${id}/show`}
      bulkActionButtons={false}
    >
      <TextField source="patientId" label="ID" />
      <TextField source="accountNo" label="Account #" />
      <FunctionField
        label="Patient"
        render={(r: PatientRow) =>
          `${r.lastName ?? ''}, ${r.firstName ?? ''}`.replace(/^, |, $/, '').trim()
        }
      />
      <TextField source="sex" label="Sex" />
      <TextField source="city" label="City" />
      <TextField source="state" label="State" />
      <TextField source="primaryDoctor" label="Primary Doctor" />
    </Datagrid>
  </List>
)

export default PatientList
