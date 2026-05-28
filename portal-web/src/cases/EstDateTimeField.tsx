import { DateField, type DateFieldProps } from 'react-admin'

// Renders a UTC timestamp in US Eastern time (handles EST/EDT automatically).
const EST_OPTIONS: Intl.DateTimeFormatOptions = {
  timeZone: 'America/New_York',
  year: 'numeric',
  month: 'numeric',
  day: 'numeric',
  hour: 'numeric',
  minute: '2-digit',
  timeZoneName: 'short',
}

const EstDateTimeField = (props: DateFieldProps) => (
  <DateField {...props} showTime options={EST_OPTIONS} />
)

export default EstDateTimeField
