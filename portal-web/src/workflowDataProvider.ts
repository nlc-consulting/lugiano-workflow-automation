import simpleRestProvider from 'ra-data-simple-rest'
import { fetchUtils } from 'react-admin'

// Talks to the .NET Workflow API (read-only WorkflowAutomation data).
// Proxied by Vite at /workflow-api -> http://localhost:5100 (see vite.config.ts).
const workflowApiUrl = import.meta.env.VITE_WORKFLOW_API_URL || '/workflow-api'

// The Workflow API is open on localhost (no JWT); just set Accept.
const httpClient = (url: string, options: fetchUtils.Options = {}) => {
  const headers = options.headers ? new Headers(options.headers) : new Headers()
  headers.set('Accept', 'application/json')
  return fetchUtils.fetchJson(url, { ...options, headers })
}

const workflowDataProvider = simpleRestProvider(workflowApiUrl, httpClient)

export default workflowDataProvider
