import { lazy, Suspense, useState, type ReactNode } from 'react'
import { BrowserRouter, Navigate, Route, Routes, useLocation } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ApiError } from './lib/api'
import { AuthProvider, useAuth } from './lib/auth'
import { hasRole, type TenantRole } from './lib/types'
import { AppShell } from './components/AppShell'
import { ToastProvider } from './components/toast'
import { Spinner } from './components/ui'
import { LoginPage, RegisterPage, AcceptInvitationPage } from './pages/AuthPages'

// Route-level code splitting: each page loads on first visit.
const DashboardPage = lazy(() => import('./pages/DashboardPage'))
const ChatPage = lazy(() => import('./pages/ChatPage'))
const ConversationsPage = lazy(() => import('./pages/ConversationsPage'))
const DocumentsPage = lazy(() => import('./pages/DocumentsPage'))
const TicketsPage = lazy(() => import('./pages/TicketsPage'))
const EvaluationsPage = lazy(() => import('./pages/EvaluationsPage'))
const TeamPage = lazy(() => import('./pages/TeamPage'))
const SettingsPage = lazy(() => import('./pages/SettingsPage'))

export function createQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: {
        staleTime: 15_000,
        // Don't retry what retrying can't fix (4xx); retry transient failures once.
        retry: (count, error) => !(error instanceof ApiError && error.status < 500) && count < 1,
        refetchOnWindowFocus: false,
      },
    },
  })
}

function RequireAuth({ children, minRole = 'Viewer' }: { children: ReactNode; minRole?: TenantRole }) {
  const { status, me } = useAuth()
  const location = useLocation()
  if (status === 'loading') return <Spinner label="Restoring session" />
  if (status === 'anonymous') return <Navigate to="/login" replace state={{ from: location.pathname }} />
  // UI gating only improves UX; the API enforces every permission independently.
  if (!hasRole(me?.role, minRole)) return <Navigate to="/dashboard" replace />
  return <>{children}</>
}

function PublicOnly({ children }: { children: ReactNode }) {
  const { status } = useAuth()
  if (status === 'loading') return <Spinner label="Restoring session" />
  return status === 'authenticated' ? <Navigate to="/dashboard" replace /> : <>{children}</>
}

export function AppRoutes() {
  return (
    <Suspense fallback={<Spinner />}>
      <Routes>
        <Route path="/login" element={<PublicOnly><LoginPage /></PublicOnly>} />
        <Route path="/register" element={<PublicOnly><RegisterPage /></PublicOnly>} />
        <Route path="/accept-invite" element={<AcceptInvitationPage />} />
        <Route element={<RequireAuth><AppShell /></RequireAuth>}>
          <Route path="/dashboard" element={<DashboardPage />} />
          <Route path="/chat" element={<ChatPage />} />
          <Route path="/chat/:conversationId" element={<ChatPage />} />
          <Route path="/conversations" element={<ConversationsPage />} />
          <Route path="/documents" element={<DocumentsPage />} />
          <Route path="/tickets" element={<TicketsPage />} />
          <Route path="/evaluations" element={<RequireAuth minRole="Admin"><EvaluationsPage /></RequireAuth>} />
          <Route path="/team" element={<TeamPage />} />
          <Route path="/settings" element={<SettingsPage />} />
        </Route>
        <Route path="*" element={<Navigate to="/dashboard" replace />} />
      </Routes>
    </Suspense>
  )
}

export default function App() {
  const [queryClient] = useState(createQueryClient)
  return (
    <QueryClientProvider client={queryClient}>
      <ToastProvider>
        <AuthProvider>
          <BrowserRouter>
            <AppRoutes />
          </BrowserRouter>
        </AuthProvider>
      </ToastProvider>
    </QueryClientProvider>
  )
}
