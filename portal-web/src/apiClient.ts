import { fetchUtils, HttpError } from 'react-admin'

export const API_BASE_URL = import.meta.env.VITE_API_URL || '/api'

export const getAuthToken = () => localStorage.getItem('auth')
export const getRefreshToken = () => localStorage.getItem('refresh')

export const setAuthTokens = (accessToken: string, refreshToken: string) => {
  localStorage.setItem('auth', accessToken)
  localStorage.setItem('refresh', refreshToken)
}

export const clearAuthTokens = () => {
  localStorage.removeItem('auth')
  localStorage.removeItem('refresh')
}

export const withAuthHeaders = (headers?: HeadersInit) => {
  const next = new Headers(headers)
  const token = getAuthToken()
  if (token) {
    next.set('Authorization', `Bearer ${token}`)
  }
  return next
}

// For URLs opened via window.open / direct browser navigation (PDF previews,
// downloads), the browser can't send the Authorization header — so carry the
// JWT as an `access_token` query param. The workflow API reads it from there too.
export const withWorkflowToken = (url: string): string => {
  const token = getAuthToken()
  if (!token) return url
  return url + (url.includes('?') ? '&' : '?') + 'access_token=' + encodeURIComponent(token)
}

let refreshPromise: Promise<string | null> | null = null

export const refreshAccessToken = async () => {
  const refreshToken = getRefreshToken()
  if (!refreshToken) {
    clearAuthTokens()
    return null
  }

  if (refreshPromise) {
    return refreshPromise
  }

  refreshPromise = (async () => {
    try {
      const response = await fetch(`${API_BASE_URL}/auth/refresh`, {
        method: 'POST',
        headers: new Headers({ 'Content-Type': 'application/json' }),
        body: JSON.stringify({ refreshToken }),
      })
      if (!response.ok) {
        throw new Error('Refresh failed')
      }
      const data = (await response.json()) as {
        access_token?: string
        refresh_token?: string
      }
      if (!data.access_token || !data.refresh_token) {
        throw new Error('Invalid refresh response')
      }
      setAuthTokens(data.access_token, data.refresh_token)
      return data.access_token
    } catch {
      clearAuthTokens()
      return null
    } finally {
      refreshPromise = null
    }
  })()

  return refreshPromise
}

const needsRefresh = (error: unknown) => {
  if (error instanceof HttpError) {
    return error.status === 401 || error.status === 403
  }
  if (typeof error === 'object' && error && 'status' in error) {
    const status = (error as { status?: number }).status
    return status === 401 || status === 403
  }
  return false
}

export const authFetchJson = (url: string, options: fetchUtils.Options = {}) => {
  const headers = withAuthHeaders(options.headers)
  return fetchUtils.fetchJson(url, { ...options, headers }).catch(async (error) => {
    if (!needsRefresh(error)) {
      throw error
    }
    const token = await refreshAccessToken()
    if (!token) {
      throw error
    }
    const retryHeaders = withAuthHeaders(options.headers)
    return await fetchUtils.fetchJson(url, { ...options, headers: retryHeaders })
  })
}
