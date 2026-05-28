import simpleRestProvider from 'ra-data-simple-rest'
import { fetchUtils } from 'react-admin'
import { authFetchJson } from './apiClient'

// Use env var in production, fallback to proxy (/api) locally.
const apiBaseUrl = import.meta.env.VITE_API_URL || '/api'

const httpClient = (url: string, options: fetchUtils.Options = {}) => {
  const headers = options.headers ? new Headers(options.headers) : new Headers()
  headers.set('Accept', 'application/json')
  return authFetchJson(url, { ...options, headers })
}

const dataProvider = simpleRestProvider(apiBaseUrl, httpClient)

export default dataProvider
