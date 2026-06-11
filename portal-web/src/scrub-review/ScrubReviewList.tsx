import {
  Datagrid,
  FunctionField,
  List,
  TextField,
  type RaRecord,
} from 'react-admin'
import EstDateTimeField from '../cases/EstDateTimeField'

type ScrubReviewRow = RaRecord & {
  firstName?: string
  lastName?: string
  latestScrubAt?: string | null
  summary?: string | null
}

// Triage queue for failed scrubs. Click → existing case detail.
// Sort is server-side (latest first); no client controls so staff just works
// the top of the list.
const ScrubReviewList = () => (
  <List
    title="Scrub Review"
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
        render={(r: ScrubReviewRow) =>
          `${r.lastName ?? ''}, ${r.firstName ?? ''}`.replace(/^, |, $/, '').trim()
        }
      />
      <EstDateTimeField source="latestScrubAt" label="Last scrub" />
      <TextField source="summary" label="Reason" />
    </Datagrid>
  </List>
)

export default ScrubReviewList
