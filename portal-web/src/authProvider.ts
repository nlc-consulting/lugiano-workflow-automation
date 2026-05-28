import type { AuthProvider } from 'react-admin'
import {
  API_BASE_URL,
  clearAuthTokens,
  getAuthToken,
  getRefreshToken,
  refreshAccessToken,
  setAuthTokens,
} from './apiClient'

export const decodeJwtPayload = (token: string) => {
  try {
    const [, payload] = token.split('.')
    if (!payload) {
      return null
    }
    const decoded = atob(payload.replace(/-/g, '+').replace(/_/g, '/'))
    return JSON.parse(decoded) as { exp?: number; role?: string; sub?: string }
  } catch {
    return null
  }
}

export const getAuthPayload = () => {
  const token = getAuthToken()
  if (!token) {
    return null
  }
  return decodeJwtPayload(token)
}

export const authProvider: AuthProvider = {
  login: async ({ username, password }: { username: string; password: string }) => {
    const response = await fetch(`${API_BASE_URL}/auth/login`, {
      method: 'POST',
      headers: new Headers({ 'Content-Type': 'application/json' }),
      body: JSON.stringify({ username, password }),
    })

    if (!response.ok) {
      throw new Error('Invalid username or password')
    }

    const data = (await response.json()) as {
      access_token?: string
      refresh_token?: string
    }
    if (!data.access_token || !data.refresh_token) {
      throw new Error('Auth tokens missing')
    }

    setAuthTokens(data.access_token, data.refresh_token)
  },
  logout: () => {
    clearAuthTokens()
    return Promise.resolve()
  },
  checkAuth: () => {
    const token = getAuthToken()
    const refreshToken = getRefreshToken()
    if (!token && !refreshToken) {
      return Promise.reject()
    }

    if (!token && refreshToken) {
      return refreshAccessToken().then((newToken) =>
        newToken ? Promise.resolve() : Promise.reject(),
      )
    }

    if (!token) {
      return Promise.reject()
    }

    const payload = decodeJwtPayload(token)
    if (payload?.exp && payload.exp * 1000 < Date.now()) {
      return refreshAccessToken().then((newToken) =>
        newToken ? Promise.resolve() : Promise.reject(),
      )
    }

    return Promise.resolve()
  },
  checkError: (error: { status?: number }) => {
    if (error?.status === 401 || error?.status === 403) {
      return refreshAccessToken().then((newToken) =>
        newToken ? Promise.resolve() : Promise.reject(),
      )
    }
    return Promise.resolve()
  },
  getPermissions: () => {
    const payload = getAuthPayload()
    return Promise.resolve(payload?.role ?? null)
  },
}
