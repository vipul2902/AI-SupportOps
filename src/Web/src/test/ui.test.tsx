import { describe, expect, it, vi, beforeEach } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { QueryClientProvider } from '@tanstack/react-query'
import { createQueryClient } from '../App'
import { AuthProvider } from '../lib/auth'
import { tokens } from '../lib/api'
import { ToastProvider } from '../components/toast'
import { AppShell } from '../components/AppShell'
import { LoginPage } from '../pages/AuthPages'
import ChatPage from '../pages/ChatPage'
import type { Me, TenantRole } from '../lib/types'

const me = (role: TenantRole): Me => ({
  userId: 'u1', email: 'jane@acme.test', displayName: 'Jane Doe', tenantId: 't1', role,
  memberships: [{ tenantId: 't1', tenantName: 'Acme', role }],
})

const sse = (events: [string, unknown][]) =>
  new Response(events.map(([type, data]) => `event: ${type}\ndata: ${JSON.stringify(data)}\n\n`).join(''), {
    headers: { 'Content-Type': 'text/event-stream' },
  })

/** Routes fetch calls to handlers by "METHOD path" prefix. */
function mockApi(handlers: Record<string, (init?: RequestInit) => Response | Promise<Response>>) {
  return vi.spyOn(globalThis, 'fetch').mockImplementation(async (input, init) => {
    const key = `${init?.method ?? 'GET'} ${String(input)}`
    const match = Object.keys(handlers).find(k => key.startsWith(k))
    if (!match) throw new Error(`Unexpected request: ${key}`)
    return handlers[match](init)
  })
}

function renderApp(ui: React.ReactNode, path: string, routePath = '*') {
  return render(
    <QueryClientProvider client={createQueryClient()}>
      <ToastProvider>
        <AuthProvider>
          <MemoryRouter initialEntries={[path]}>
            <Routes>
              <Route path={routePath} element={ui} />
              <Route path="/dashboard" element={<p>Dashboard home</p>} />
            </Routes>
          </MemoryRouter>
        </AuthProvider>
      </ToastProvider>
    </QueryClientProvider>,
  )
}

const signedIn = { refreshToken: 'r1', accessToken: 'a1', accessTokenExpiresAt: '', refreshTokenExpiresAt: '', tenantId: 't1', role: 'Viewer' as const }

beforeEach(() => {
  vi.restoreAllMocks()
  tokens.clear()
})

describe('navigation', () => {
  it.each([
    ['Viewer', false],
    ['Admin', true],
  ] as const)('shows admin-only items only to the right roles (%s)', async (role, seesEvaluations) => {
    tokens.set(signedIn)
    mockApi({
      'POST /api/auth/refresh': () => Response.json(signedIn),
      'GET /api/me': () => Response.json(me(role)),
    })

    renderApp(<AppShell />, '/x')

    expect(await screen.findByRole('link', { name: /AI Assistant/ })).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: /Evaluations/ }) !== null).toBe(seesEvaluations)
    expect(screen.getAllByText('Acme').length).toBeGreaterThan(0) // sidebar switcher + mobile header
  })
})

describe('login', () => {
  it('shows the API error and then signs in', async () => {
    const user = userEvent.setup()
    let attempts = 0
    mockApi({
      'POST /api/auth/login': () => (++attempts === 1
        ? Response.json({ title: 'Unauthorized', detail: 'Invalid email or password.' }, { status: 401 })
        : Response.json(signedIn)),
      'GET /api/me': () => Response.json(me('Owner')),
    })

    renderApp(<LoginPage />, '/login', '/login')
    await user.type(await screen.findByLabelText('Email'), 'jane@acme.test')
    await user.type(screen.getByLabelText('Password'), 'wrong-password')
    await user.click(screen.getByRole('button', { name: 'Sign in' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Invalid email or password.')

    await user.click(screen.getByRole('button', { name: 'Sign in' }))
    expect(await screen.findByText('Dashboard home')).toBeInTheDocument()
  })
})

describe('chat', () => {
  it('streams an answer and renders citations', async () => {
    const user = userEvent.setup()
    tokens.set(signedIn)
    let resolveStream!: () => void
    const streamGate = new Promise<void>(r => { resolveStream = r })
    mockApi({
      'POST /api/auth/refresh': () => Response.json(signedIn),
      'GET /api/me': () => Response.json(me('Viewer')),
      'GET /api/conversations?': () => Response.json({ items: [], page: 1, pageSize: 50, totalCount: 0 }),
      'GET /api/conversations/c1': () => streamGate.then(() => Response.json({ id: 'c1', title: 'Q', createdAt: '', summary: null, messages: [] })),
      'POST /api/chat': () => sse([
        ['meta', { conversationId: 'c1', userMessageId: 'u1', conversationTitle: 'How do I reset?' }],
        ['delta', { text: 'Open **Settings** ' }],
        ['delta', { text: 'and click Reset [1]' }],
        ['done', {
          messageId: 'm1', outcome: 'Answered', invalidCitationNumbers: [], retrievalQuery: 'How do I reset?', model: 'fake', inputTokens: 10, outputTokens: 5, latencyMs: 20,
          citations: [{ number: 1, documentId: 'd1', fileName: 'security.md', pageNumber: null, heading: 'Account security', score: 0.82, snippet: 'To reset your password…' }],
        }],
      ]),
    })

    renderApp(<ChatPage />, '/chat', '/chat/:conversationId?')
    await user.type(await screen.findByLabelText('Message'), 'How do I reset?')
    await user.click(screen.getByRole('button', { name: 'Send' }))

    expect(await screen.findByText('Settings', { selector: 'strong' })).toBeInTheDocument()
    expect(screen.getByText('security.md')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Source 1' })).toHaveAttribute('href', '#cite-pending-1')
    expect(screen.getByText('How do I reset?')).toBeInTheDocument()
    resolveStream()
    await waitFor(() => expect(screen.queryByText('Searching the knowledge base…')).not.toBeInTheDocument())
  })
})
