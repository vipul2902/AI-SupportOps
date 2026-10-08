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

/**
 * Session tokens.
 * - Access token: memory only, short-lived (15 min).
 * - Refresh token: never touched by JavaScript. The API keeps it in an HttpOnly, Secure,
 *   SameSite=Strict cookie scoped to /api/auth, which script (including injected script) cannot read.
 * Every auth call opts into that mode with the X-Auth-Mode header, which also makes the cookie
 * CSRF-safe: a cross-site page cannot send a custom header without a CORS preflight the API never allows.
 */
export const AUTH_MODE_HEADERS = { 'X-Auth-Mode': 'cookie' } as const

let accessToken: string | null = null
let onSessionEnded: (() => void) | null = null

export const tokens = {
  get access() { return accessToken },
  set(auth: AuthResponse) { accessToken = auth.accessToken },
  clear() { accessToken = null },
  onSessionEnded(handler: () => void) { onSessionEnded = handler },
}

// ---- refresh (single-flight) ----
// Several requests can fail with 401 at once when the access token expires. Refresh tokens rotate and
// the API revokes the whole session if a rotated token is reused, so N parallel refreshes would log the
// user out. All callers therefore await the same in-flight refresh.
let refreshing: Promise<boolean> | null = null

export function refreshSession(): Promise<boolean> {
  refreshing ??= (async () => {
    try {
      const response = await fetch('/api/auth/refresh', { method: 'POST', headers: AUTH_MODE_HEADERS, credentials: 'same-origin' })
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
    if (path.startsWith('/api/auth/')) Object.entries(AUTH_MODE_HEADERS).forEach(([k, v]) => headers.set(k, v))
    if (init.body && !(init.body instanceof FormData) && !headers.has('Content-Type')) {
      headers.set('Content-Type', 'application/json')
    }
    return fetch(path, { ...init, headers, credentials: 'same-origin' })
  }

  let response = await send()
  // Only retry requests that were authenticated: a 401 from sign-in itself means wrong credentials.
  if (response.status === 401 && tokens.access) {
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
