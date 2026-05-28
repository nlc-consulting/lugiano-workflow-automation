import {
  ArrayField,
  BooleanField,
  Datagrid,
  DateField,
  FunctionField,
  ListButton,
  Show,
  SimpleShowLayout,
  TextField,
  TopToolbar,
  useRecordContext,
  type RaRecord,
} from 'react-admin'
import { Box, Typography } from '@mui/material'
import ArrowBack from '@mui/icons-material/ArrowBack'
import PipDateEditor from './PipDateEditor'
import EstDateTimeField from './EstDateTimeField'

// Expandable panel showing a note's reconstructed plain text.
const NoteText = () => {
  const note = useRecordContext<RaRecord & { plainText?: string }>()
  if (!note?.plainText) {
    return (
      <Box sx={{ p: 2 }}>
        <Typography variant="body2" color="text.secondary">
          No note text available.
        </Typography>
      </Box>
    )
  }
  return (
    <Box sx={{ p: 2, whiteSpace: 'pre-wrap', fontFamily: 'monospace', fontSize: 12 }}>
      {note.plainText}
    </Box>
  )
}

const join = (...parts: (string | undefined)[]) => parts.filter(Boolean).join(' ')

// Quick way back to the top of the patient list.
const ShowActions = () => (
  <TopToolbar>
    <ListButton label="Back to patients" icon={<ArrowBack />} />
  </TopToolbar>
)

const CaseShow = () => (
  <Show title="Patient detail" actions={<ShowActions />}>
    <SimpleShowLayout>
      <FunctionField
        label="Patient"
        render={(r: RaRecord) => join(r.firstName, r.middleName, r.lastName)}
      />
      <TextField source="patientId" label="Patient ID" />
      <TextField source="sex" label="Sex" />
      <FunctionField
        label="Address"
        render={(r: RaRecord) =>
          [r.address, r.city, r.state, r.zip].filter(Boolean).join(', ')
        }
      />
      <TextField source="primaryDoctor" label="Primary doctor" />
      <TextField source="currentState" label="Workflow state" />

      <BooleanField source="insuranceProvided" label="Insurance" />
      <EstDateTimeField source="insuranceAddedAt" label="Insurance added" emptyText="—" />
      <BooleanField source="doctorNotesReceived" label="Doctor notes" />
      <EstDateTimeField source="doctorNotesReceivedAt" label="Notes received" emptyText="—" />
      <BooleanField source="pipVerified" label="PIP verified" />
      <DateField source="pipVerifiedAt" label="PIP verified date" emptyText="—" />
      <PipDateEditor />

      <EstDateTimeField source="addedAt" label="Added to portal" emptyText="—" />
      <EstDateTimeField source="lastUpdatedAt" label="Last updated" emptyText="—" />

      <ArrayField source="policies" label="Insurance policies">
        <Datagrid bulkActionButtons={false} rowClick={false}>
          <TextField source="insurer" label="Insurer" />
          <TextField source="coverageType" label="Coverage" />
          <DateField source="effectiveDate" label="Effective" />
          <DateField source="terminationDate" label="Termination" />
        </Datagrid>
      </ArrayField>

      <ArrayField source="notes" label="Recent doctor notes (expand for text)">
        <Datagrid bulkActionButtons={false} rowClick={false} expand={<NoteText />}>
          <DateField source="noteDate" label="Date" />
          <TextField source="doctor" label="Doctor" />
          <TextField source="status" label="Status" />
        </Datagrid>
      </ArrayField>
    </SimpleShowLayout>
  </Show>
)

export default CaseShow
