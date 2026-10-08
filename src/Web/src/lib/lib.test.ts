import { describe, expect, it, vi, beforeEach } from 'vitest'
import { readEventStream, parseEvent } from './sse'
import { apiFetch, ApiError, refreshSession, tokens } from './api'
import { fillDays } from './metrics'
import { humanize, percent } from './format'

function streamOf(chunks: (string | Uint8Array)[]) {
  const encoder = new TextEncoder()
  return new ReadableStream<Uint8Array>({
    start(controller) {
      chunks.forEach(c => controller.enqueue(typeof c === 'string' ? encoder.encode(c) : c))
      controller.close()
    },
  })
}

async function collect(body: ReadableStream<Uint8Array>) {
  const events = []
  for await (const e of readEventStream(body)) events.push(e)
  return events
}

describe('SSE parsing', () => {
  it('parses events split across network chunks and CRLF line endings', async () => {
    const events = await collect(streamOf([
      'event: meta\r\ndata: {"conversationId":"c1","userMessageId":"u1","conversationTitle":"T"}\r\n\r\nevent: del',
      'ta\ndata: {"text":"Hel"}\n\nevent: delta\ndata: {"text":"lo"}\n\n',
      'event: done\ndata: {"messageId":"m1","outcome":"Answered","citations":[]}\n\n',
    ]))

    expect(events.map(e => e.type)).toEqual(['meta', 'delta', 'delta', 'done'])
    expect(events.filter(e => e.type === 'delta').map(e => (e.data as { text: string }).text).join('')).toBe('Hello')
  })

  it('keeps a multi-byte character intact when its bytes are split between chunks', async () => {
    const bytes = new TextEncoder().encode('event: delta\ndata: {"text":"€"}\n\n')
    const euroStart = bytes.indexOf(0xe2)
    const events = await collect(streamOf([bytes.slice(0, euroStart + 1), bytes.slice(euroStart + 1)]))

    expect(events).toEqual([{ type: 'delta', data: { text: '€' } }])
  })

  it('ignores malformed or data-less blocks', () => {
    expect(parseEvent('event: delta\ndata: {not json')).toBeNull()
    expect(parseEvent(': keep-alive comment')).toBeNull()
  })
})

describe('API client', () => {
  const auth = (n: number) => ({ accessToken: `access-${n}`, refreshToken: `refresh-${n}`, accessTokenExpiresAt: '', refreshTokenExpiresAt: '', tenantId: 't', role: 'Owner' as const })

  beforeEach(() => {
    tokens.clear()
    vi.restoreAllMocks()
  })

  it('refreshes once for concurrent 401s and retries each request with the new token', async () => {
    tokens.set(auth(1))
    let refreshCalls = 0
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation(async (input, init) => {
      const url = String(input)
      if (url === '/api/auth/refresh') {
        refreshCalls++
        await new Promise(r => setTimeout(r, 10))
        return Response.json(auth(2))
      }
      const authorization = new Headers(init?.headers).get('Authorization')
      return authorization === 'Bearer access-2' ? Response.json({ ok: url }) : new Response(null, { status: 401 })
    })

    const results = await Promise.all([apiFetch('/api/a'), apiFetch('/api/b'), apiFetch('/api/c')])

    expect(results.every(r => r.ok)).toBe(true)
    // Rotating refresh tokens: a second, parallel refresh with the same token would revoke the session.
    expect(refreshCalls).toBe(1)
    expect(tokens.access).toBe('access-2')
    expect(fetchMock).toHaveBeenCalledTimes(7) // 3 failures + 1 refresh + 3 retries
  })

  it('ends the session when the refresh token is rejected', async () => {
    tokens.set(auth(1))
    const ended = vi.fn()
    tokens.onSessionEnded(ended)
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(null, { status: 401 }))

    await expect(apiFetch('/api/me')).rejects.toBeInstanceOf(ApiError)
    expect(ended).toHaveBeenCalledOnce()
    expect(tokens.access).toBeNull()
  })

  it('surfaces ProblemDetails and validation messages', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(Response.json(
      { title: 'One or more validation errors occurred.', status: 400, errors: { Password: ['Password must be at least 12 characters.'] } },
      { status: 400 }))

    const error = (await apiFetch('/api/auth/register', { method: 'POST', body: '{}' }).catch((e: unknown) => e)) as ApiError
    expect(error.status).toBe(400)
    expect(error.message).toBe('Password must be at least 12 characters.')
  })

  it('refreshes via the httpOnly cookie: opt-in header, no token in JavaScript', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(Response.json({ ...auth(3), refreshToken: '' }))

    expect(await refreshSession()).toBe(true)

    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe('/api/auth/refresh')
    expect(new Headers(init?.headers).get('X-Auth-Mode')).toBe('cookie')
    expect(init?.body).toBeUndefined()
    expect(init?.credentials).toBe('same-origin')
    expect(localStorage.length).toBe(0)
  })

  it('does not retry a failed sign-in as an expired session', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(Response.json({ detail: 'Invalid email or password.' }, { status: 401 }))

    await expect(apiFetch('/api/auth/login', { method: 'POST', body: '{}' })).rejects.toThrow('Invalid email or password.')
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })
})

describe('formatting', () => {
  it('fills missing days with zeros so the time axis is honest', () => {
    const days = fillDays([{ date: '2026-10-07', questions: 4, answered: 3, averageLatencyMs: 120 }], 3, new Date('2026-10-08T12:00:00Z'))
    expect(days.map(d => [d.date, d.questions])).toEqual([['2026-10-06', 0], ['2026-10-07', 4], ['2026-10-08', 0]])
  })

  it('humanizes enum values and formats rates', () => {
    expect(humanize('InProgress')).toBe('In progress')
    expect(humanize('NoRelevantSources')).toBe('No relevant sources')
    expect(percent(0.923)).toBe('92%')
    expect(percent(null)).toBe('—')
  })
})
