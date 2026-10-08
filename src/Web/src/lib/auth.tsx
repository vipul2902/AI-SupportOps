import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { api, json, refreshSession, tokens } from './api'
import type { AuthResponse, Me } from './types'

interface AuthState {
  status: 'loading' | 'authenticated' | 'anonymous'
  me: Me | null
  login: (email: string, password: string) => Promise<void>
  register: (input: { email: string; password: string; displayName: string; organizationName: string }) => Promise<void>
  acceptInvitation: (input: { token: string; password: string; displayName?: string }) => Promise<void>
  switchTenant: (tenantId: string) => Promise<void>
  logout: () => Promise<void>
}

const AuthContext = createContext<AuthState | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient()
  const [me, setMe] = useState<Me | null>(null)
  const [status, setStatus] = useState<AuthState['status']>('loading')

  const loadMe = useCallback(async () => {
    setMe(await api<Me>('/api/me'))
    setStatus('authenticated')
  }, [])

  const endSession = useCallback(() => {
    tokens.clear()
    queryClient.clear()
    setMe(null)
    setStatus('anonymous')
  }, [queryClient])

  // On load: a stored refresh token means a previous session; exchange it for an access token.
  useEffect(() => {
    tokens.onSessionEnded(endSession)
    void (async () => {
      if (tokens.refresh && (await refreshSession())) {
        try {
          await loadMe()
          return
        } catch { /* fall through to anonymous */ }
      }
      endSession()
    })()
  }, [endSession, loadMe])

  const start = useCallback(async (auth: AuthResponse) => {
    tokens.set(auth)
    queryClient.clear() // never show cached data from another user or organization
    await loadMe()
  }, [loadMe, queryClient])

  const value = useMemo<AuthState>(() => ({
    status,
    me,
    login: async (email, password) =>
      start(await api<AuthResponse>('/api/auth/login', { method: 'POST', body: json({ email, password }) })),
    register: async input =>
      start(await api<AuthResponse>('/api/auth/register', { method: 'POST', body: json(input) })),
    acceptInvitation: async input =>
      start(await api<AuthResponse>('/api/auth/accept-invitation', { method: 'POST', body: json(input) })),
    switchTenant: async tenantId =>
      start(await api<AuthResponse>('/api/auth/switch-tenant', { method: 'POST', body: json({ tenantId }) })),
    logout: async () => {
      const refreshToken = tokens.refresh
      if (refreshToken) {
        try { await api('/api/auth/logout', { method: 'POST', body: json({ refreshToken }) }) } catch { /* best effort */ }
      }
      endSession()
    },
  }), [status, me, start, endSession])

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

export function useAuth(): AuthState {
  const context = useContext(AuthContext)
  if (!context) throw new Error('useAuth must be used inside <AuthProvider>')
  return context
}
