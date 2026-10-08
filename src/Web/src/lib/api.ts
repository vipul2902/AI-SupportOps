import type { AuthResponse, Problem } from './types'

/** An HTTP error carrying the API's RFC 7807 ProblemDetails. */
export class ApiError extends Error {
  readonly status: number
  readonly problem: Problem

  constructor(status: number, problem: Problem) {
    super(ApiError.describe(status, problem))
    this.status = status
    this.problem = problem
  }

  private static describe(status: number, p: Problem): string {
    const validation = p.errors ? Object.values(p.errors).flat().join(' ') : ''
    return validation || p.detail || p.title || `Request failed (${status})`
  }
}

// ---- token storage ----
// Access token: memory only (short-lived, never persisted). Refresh token: localStorage so a reload
// keeps the session. Trade-off: readable by injected script (XSS). The stronger alternative is an
// httpOnly SameSite cookie set by the API; documented in docs/security.md.
const REFRESH_KEY = 'aiso.refresh'
let accessToken: string | null = null
let onSessionEnded: (() => void) | null = null

export const tokens = {
  get access() { return accessToken },
  get refresh() {
    try { return localStorage.getItem(REFRESH_KEY) } catch { return null }
  },
  set(auth: AuthResponse) {
    accessToken = auth.accessToken
    try { localStorage.setItem(REFRESH_KEY, auth.refreshToken) } catch { /* storage unavailable */ }
  },
  clear() {
    accessToken = null
    try { localStorage.removeItem(REFRESH_KEY) } catch { /* ignore */ }
  },
  onSessionEnded(handler: () => void) { onSessionEnded = handler },
}

// ---- refresh (single-flight) ----
// Several requests can fail with 401 at once when the access token expires. Refresh tokens rotate and
// the API revokes the whole session if a rotated token is reused, so N parallel refreshes would log
// the user out. All callers therefore await the same in-flight refresh.
let refreshing: Promise<boolean> | null = null

export function refreshSession(): Promise<boolean> {
  refreshing ??= (async () => {
    const refreshToken = tokens.refresh
    if (!refreshToken) return false
    try {
      const response = await fetch('/api/auth/refresh', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ refreshToken }),
      })
      if (!response.ok) {
        tokens.clear()
        return false
      }
      tokens.set((await response.json()) as AuthResponse)
      return true
    } catch {
      return false
    } finally {
      refreshing = null
    }
  })()
  return refreshing
}

/** fetch with auth, one transparent refresh-and-retry on 401, and ProblemDetails errors. */
export async function apiFetch(path: string, init: RequestInit = {}): Promise<Response> {
  const send = () => {
    const headers = new Headers(init.headers)
    if (tokens.access) headers.set('Authorization', `Bearer ${tokens.access}`)
    if (init.body && !(init.body instanceof FormData) && !headers.has('Content-Type')) {
      headers.set('Content-Type', 'application/json')
    }
    return fetch(path, { ...init, headers })
  }

  let response = await send()
  if (response.status === 401 && tokens.refresh) {
    if (await refreshSession()) {
      response = await send()
    } else {
      onSessionEnded?.()
    }
  }

  if (!response.ok) {
    let problem: Problem = {}
    try { problem = (await response.json()) as Problem } catch { /* non-JSON error body */ }
    throw new ApiError(response.status, problem)
  }
  return response
}

export async function api<T>(path: string, init: RequestInit = {}): Promise<T> {
  const response = await apiFetch(path, init)
  if (response.status === 204) return undefined as T
  return (await response.json()) as T
}

export const json = (body: unknown): RequestInit['body'] => JSON.stringify(body)
