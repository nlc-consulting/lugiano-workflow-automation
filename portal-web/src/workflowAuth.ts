// The .NET workflow API now requires the portal JWT. Rather than touch every
// `fetch(`${WORKFLOW_API}...`)` call site (and risk missing one — a security
// hole), we patch window.fetch once: any request to /workflow-api gets the
// bearer token attached, with a single refresh+retry on 401/403. Imported
// first in main.tsx so the patch is in place before any request fires.
import { getAuthToken, refreshAccessToken } from './apiClient'

const originalFetch = window.fetch.bind(window)

const urlOf = (input: RequestInfo | URL): string =>
  typeof input === 'string' ? input : input instanceof URL ? input.href : input.url

const withToken = (
  input: RequestInfo | URL,
  init: RequestInit | undefined,
  token: string | null,
): RequestInit => {
  const headers = new Headers(
    init?.headers ?? (input instanceof Request ? input.headers : undefined),
  )
  if (token) headers.set('Authorization', `Bearer ${token}`)
  return { ...init, headers }
}

window.fetch = (async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
  if (!urlOf(input).includes('/workflow-api')) {
    return originalFetch(input, init)
  }
  let res = await originalFetch(input, withToken(input, init, getAuthToken()))
  if (res.status === 401 || res.status === 403) {
    const fresh = await refreshAccessToken()
    if (fresh) res = await originalFetch(input, withToken(input, init, fresh))
  }
  return res
}) as typeof window.fetch
