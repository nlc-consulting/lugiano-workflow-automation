import {
  Datagrid,
  FunctionField,
  List,
  TextField,
  type RaRecord,
} from 'react-admin'
import EstDateTimeField from '../cases/EstDateTimeField'

type HumanReviewRow = RaRecord & {
  firstName?: string
  lastName?: string
  latestScrubAt?: string | null
  summary?: string | null
}

// Escalation queue: a doctor already corrected the failing note through the
// portal, and the new note STILL failed scrub. A human reviewer needs to
// either override the verdict ('mark scrubbed') or work the note manually.
const HumanReviewList = () => (
  <List
    title="Human Review"
    sort={{ field: 'latestScrubAt', order: 'DESC' }}
    exporter={false}
  >
    <Datagrid
      rowClick={(id) => `/cases/${id}/show`}
      bulkActionButtons={false}
    >
      <TextField source="patientId" label="Patient ID" />
      <FunctionField
        label="Patient"
        render={(r: HumanReviewRow) =>
          `${r.lastName ?? ''}, ${r.firstName ?? ''}`.replace(/^, |, $/, '').trim()
        }
      />
      <EstDateTimeField source="latestScrubAt" label="Last scrub" />
      <TextField source="summary" label="Reason" />
    </Datagrid>
  </List>
)

export default HumanReviewList
