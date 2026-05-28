import {
  BooleanField,
  Datagrid,
  DateField,
  EmailField,
  List,
  TextField,
} from 'react-admin'

const UserList = () => (
  <List>
    <Datagrid rowClick={false} bulkActionButtons={false}>
      <TextField source="id" />
      <EmailField source="email" />
      <TextField source="fullName" label="Name" />
      <TextField source="role" />
      <TextField source="office" />
      <BooleanField source="isActive" label="Active" />
      <DateField source="createdAt" label="Created" />
    </Datagrid>
  </List>
)

export default UserList
