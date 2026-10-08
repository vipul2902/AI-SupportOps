import { useCallback, useEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, Bot, ChevronDown, Loader2, Plus, Send, Sparkles, Square, ThumbsDown, ThumbsUp, Wrench } from 'lucide-react'
import { api, json } from '../lib/api'
import { useAuth } from '../lib/auth'
import { timeAgo } from '../lib/format'
import type { AgentResponse, ConversationDetail, ConversationSummary, MessageDto, Paged } from '../lib/types'
import { useChatStream } from '../lib/useChatStream'
import { AnswerMarkdown, CitationList } from '../components/AnswerMarkdown'
import { Badge, Button, cx, ErrorState, Spinner, Textarea } from '../components/ui'
import { useToast } from '../components/toast'

type Mode = 'ask' | 'agent'

export default function ChatPage() {
  const { conversationId } = useParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const toast = useToast()
  const [mode, setMode] = useState<Mode>('ask')
  const [draft, setDraft] = useState('')
  const [agentRun, setAgentRun] = useState<AgentResponse | null>(null)
  const bottomRef = useRef<HTMLDivElement>(null)

  const conversations = useQuery({
    queryKey: ['conversations'],
    queryFn: () => api<Paged<ConversationSummary>>('/api/conversations?pageSize=50'),
  })
  const conversation = useQuery({
    queryKey: ['conversation', conversationId],
    queryFn: () => api<ConversationDetail>(`/api/conversations/${conversationId}`),
    enabled: !!conversationId,
  })

  const onComplete = useCallback(async (meta: { conversationId: string } | null) => {
    void queryClient.invalidateQueries({ queryKey: ['conversations'] })
    if (meta) {
      await queryClient.invalidateQueries({ queryKey: ['conversation', meta.conversationId] })
      if (meta.conversationId !== conversationId) navigate(`/chat/${meta.conversationId}`, { replace: true })
    }
  }, [queryClient, conversationId, navigate])
  const { turn, send, stop, reset } = useChatStream(onComplete)

  // Clear the transient turn once the persisted conversation includes it.
  useEffect(() => {
    if (turn && !turn.streaming && turn.meta && conversation.data?.messages.some(m => m.id === turn.done?.messageId)) reset()
  }, [conversation.data, turn, reset])

  useEffect(() => { bottomRef.current?.scrollIntoView({ behavior: 'smooth', block: 'end' }) }, [turn?.text, conversation.data?.messages.length, agentRun])

  const agent = useMutation({
    mutationFn: (message: string) => api<AgentResponse>('/api/agent', { method: 'POST', body: json({ message, conversationId }) }),
    onSuccess: async result => {
      setAgentRun(result)
      void queryClient.invalidateQueries({ queryKey: ['conversations'] })
      void queryClient.invalidateQueries({ queryKey: ['tickets'] })
      await queryClient.invalidateQueries({ queryKey: ['conversation', result.conversationId] })
      if (result.conversationId !== conversationId) navigate(`/chat/${result.conversationId}`, { replace: true })
    },
    onError: e => toast.error(e),
  })

  const busy = !!turn?.streaming || agent.isPending

  function submit(e?: FormEvent) {
    e?.preventDefault()
    const message = draft.trim()
    if (!message || busy) return
    setDraft('')
    setAgentRun(null)
    if (mode === 'ask') void send(message, conversationId)
    else agent.mutate(message)
  }

  function onKeyDown(e: KeyboardEvent<HTMLTextAreaElement>) {
    if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); submit() }
  }

  const messages = conversationId ? conversation.data?.messages ?? [] : []
  const pendingTurn = turn && !messages.some(m => m.id === turn.done?.messageId) ? turn : null

  return (
    <div className="flex h-full">
      {/* Conversation sidebar */}
      <aside className="hidden w-72 shrink-0 flex-col border-r border-slate-200 bg-white md:flex">
        <div className="p-3">
          <Button variant="secondary" className="w-full" onClick={() => { reset(); setAgentRun(null); navigate('/chat') }}>
            <Plus className="size-4" aria-hidden /> New conversation
          </Button>
        </div>
        <nav className="min-h-0 flex-1 space-y-0.5 overflow-y-auto px-3 pb-3" aria-label="Conversations">
          {conversations.isPending && <Spinner />}
          {conversations.data?.items.map(c => (
            <Link key={c.id} to={`/chat/${c.id}`} className={cx('block rounded-lg px-3 py-2 text-sm transition',
              c.id === conversationId ? 'bg-brand-50 text-brand-700' : 'text-slate-700 hover:bg-slate-100')}>
              <span className="block truncate font-medium">{c.title}</span>
              <span className="block text-xs text-slate-400">{timeAgo(c.lastMessageAt)}</span>
            </Link>
          ))}
          {conversations.data?.items.length === 0 && <p className="px-3 py-6 text-center text-xs text-slate-400">No conversations yet</p>}
        </nav>
      </aside>

      {/* Thread */}
      <section className="flex min-w-0 flex-1 flex-col">
        <div className="min-h-0 flex-1 overflow-y-auto">
          <div className="mx-auto max-w-3xl space-y-6 px-4 py-6">
            {conversationId && conversation.isPending && <Spinner label="Loading conversation" />}
            {conversation.isError && <ErrorState error={conversation.error} retry={() => void conversation.refetch()} />}
            {!conversationId && !pendingTurn && !agent.isPending && <Welcome onPick={setDraft} />}

            {conversation.data?.summary && (
              <p className="rounded-lg border border-dashed border-slate-300 px-3 py-2 text-xs text-slate-500">
                <span className="font-medium">Earlier in this conversation:</span> {conversation.data.summary}
              </p>
            )}

            {messages.map(m => <MessageView key={m.id} message={m} conversationId={conversationId!} />)}

            {pendingTurn && (
              <>
                <UserBubble text={pendingTurn.question} />
                <AssistantShell>
                  {pendingTurn.text
                    ? <AnswerMarkdown text={pendingTurn.text} citations={pendingTurn.done?.citations ?? []} messageKey="pending" />
                    : pendingTurn.streaming && <Thinking label="Searching the knowledge base" />}
                  {pendingTurn.streaming && pendingTurn.text && <span className="ml-0.5 inline-block h-4 w-1.5 animate-pulse bg-slate-400 align-middle" aria-hidden />}
                  {pendingTurn.error && <InlineError message={pendingTurn.error} />}
                  {pendingTurn.done && <CitationList citations={pendingTurn.done.citations} messageKey="pending" />}
                </AssistantShell>
              </>
            )}

            {agent.isPending && <AssistantShell><Thinking label="Agent is working" /></AssistantShell>}
            {agentRun && <AgentSteps run={agentRun} />}
            <div ref={bottomRef} />
          </div>
        </div>

        {/* Composer */}
        <form onSubmit={submit} className="border-t border-slate-200 bg-white px-4 py-3">
          <div className="mx-auto max-w-3xl">
            <div className="mb-2 flex items-center gap-1" role="radiogroup" aria-label="Assistant mode">
              {(['ask', 'agent'] as const).map(m => (
                <button key={m} type="button" role="radio" aria-checked={mode === m} onClick={() => setMode(m)}
                  className={cx('inline-flex items-center gap-1.5 rounded-full px-3 py-1 text-xs font-medium transition',
                    mode === m ? 'bg-slate-900 text-white' : 'text-slate-600 hover:bg-slate-100')}>
                  {m === 'ask' ? <Sparkles className="size-3.5" aria-hidden /> : <Wrench className="size-3.5" aria-hidden />}
                  {m === 'ask' ? 'Ask the knowledge base' : 'Agent (can act on tickets)'}
                </button>
              ))}
            </div>
            <div className="flex items-end gap-2">
              <Textarea value={draft} onChange={e => setDraft(e.target.value)} onKeyDown={onKeyDown} rows={2} maxLength={4000}
                placeholder={mode === 'ask' ? 'Ask a question about your documentation…' : 'e.g. "Create a high-priority ticket for jane@acme.test: exports time out"'}
                aria-label="Message" className="resize-none" />
              {turn?.streaming
                ? <Button type="button" variant="secondary" onClick={stop} aria-label="Stop generating"><Square className="size-4" /></Button>
                : <Button type="submit" disabled={!draft.trim() || busy} aria-label="Send"><Send className="size-4" /></Button>}
            </div>
            <p className="mt-1.5 text-xs text-slate-400">Answers are generated from your organization's documents and can be wrong. Check the cited sources.</p>
          </div>
        </form>
      </section>
    </div>
  )
}

function Welcome({ onPick }: { onPick: (text: string) => void }) {
  const { me } = useAuth()
  const suggestions = ['How do I reset my password?', 'What is the refund policy?', 'Which identity providers does SSO support?']
  return (
    <div className="py-16 text-center">
      <div className="mx-auto mb-4 flex size-12 items-center justify-center rounded-2xl bg-brand-50 text-brand-600"><Bot className="size-6" aria-hidden /></div>
      <h2 className="text-lg font-semibold">Hi {me?.displayName.split(' ')[0]}, how can I help?</h2>
      <p className="mt-1 text-sm text-slate-500">Answers come from your knowledge base, with sources you can check.</p>
      <div className="mt-6 flex flex-wrap justify-center gap-2">
        {suggestions.map(s => (
          <button key={s} onClick={() => onPick(s)} className="rounded-full border border-slate-200 bg-white px-3 py-1.5 text-sm text-slate-600 hover:border-brand-500 hover:text-brand-700">{s}</button>
        ))}
      </div>
    </div>
  )
}

function UserBubble({ text }: { text: string }) {
  return (
    <div className="flex justify-end">
      <div className="max-w-[85%] rounded-2xl rounded-br-sm bg-brand-600 px-4 py-2.5 text-sm whitespace-pre-wrap text-white">{text}</div>
    </div>
  )
}

function AssistantShell({ children }: { children: React.ReactNode }) {
  return (
    <div className="flex gap-3">
      <div className="flex size-8 shrink-0 items-center justify-center rounded-full bg-slate-900 text-white"><Bot className="size-4" aria-hidden /></div>
      <div className="min-w-0 flex-1 rounded-2xl rounded-tl-sm border border-slate-200 bg-white px-4 py-3 shadow-sm">{children}</div>
    </div>
  )
}

function Thinking({ label }: { label: string }) {
  return <p className="flex items-center gap-2 text-sm text-slate-500"><Loader2 className="size-4 animate-spin" aria-hidden />{label}…</p>
}

function InlineError({ message }: { message: string }) {
  return <p role="alert" className="mt-2 flex items-center gap-1.5 text-sm text-red-600"><AlertTriangle className="size-4" aria-hidden />{message}</p>
}

const outcomeBadge: Record<string, { tone: 'green' | 'amber' | 'slate' | 'red' | 'brand'; label: string }> = {
  Answered: { tone: 'green', label: 'Grounded' },
  Uncited: { tone: 'amber', label: 'No citations' },
  Declined: { tone: 'slate', label: 'Not in knowledge base' },
  NoRelevantSources: { tone: 'slate', label: 'Not in knowledge base' },
  AgentCompleted: { tone: 'brand', label: 'Agent' },
  AgentIterationLimit: { tone: 'amber', label: 'Agent stopped' },
}

function MessageView({ message, conversationId }: { message: MessageDto; conversationId: string }) {
  if (message.role === 'User') return <UserBubble text={message.content} />
  const badge = message.outcome ? outcomeBadge[message.outcome] : undefined
  return (
    <AssistantShell>
      {message.content
        ? <AnswerMarkdown text={message.content} citations={message.citations} messageKey={message.id} />
        : <p className="text-sm text-slate-400 italic">No answer.</p>}
      {message.status === 'Interrupted' && <p className="mt-1 text-xs text-slate-400">Stopped before completion.</p>}
      {message.status === 'Failed' && <InlineError message="The AI service failed while answering." />}
      <CitationList citations={message.citations} messageKey={message.id} />
      <div className="mt-3 flex items-center gap-2">
        {badge && <Badge tone={badge.tone}>{badge.label}</Badge>}
        <Feedback conversationId={conversationId} messageId={message.id} />
      </div>
    </AssistantShell>
  )
}

function Feedback({ conversationId, messageId }: { conversationId: string; messageId: string }) {
  const toast = useToast()
  const [given, setGiven] = useState<boolean | null>(null)
  const mutation = useMutation({
    mutationFn: (helpful: boolean) => api(`/api/conversations/${conversationId}/messages/${messageId}/feedback`, { method: 'POST', body: json({ helpful }) }),
    onSuccess: (_, helpful) => { setGiven(helpful); toast.success('Thanks for the feedback') },
    onError: e => toast.error(e),
  })
  return (
    <div className="ml-auto flex items-center gap-0.5">
      {[true, false].map(helpful => (
        <button key={String(helpful)} onClick={() => mutation.mutate(helpful)} disabled={mutation.isPending}
          aria-label={helpful ? 'Helpful' : 'Not helpful'} aria-pressed={given === helpful}
          className={cx('rounded p-1.5 transition hover:bg-slate-100', given === helpful ? 'text-brand-600' : 'text-slate-400')}>
          {helpful ? <ThumbsUp className="size-3.5" /> : <ThumbsDown className="size-3.5" />}
        </button>
      ))}
    </div>
  )
}

const stepTone: Record<string, 'green' | 'red' | 'amber' | 'slate'> = {
  Succeeded: 'green', Denied: 'red', InvalidArguments: 'amber', Rejected: 'amber', Failed: 'red', BudgetExceeded: 'amber', UnknownTool: 'red',
}

function AgentSteps({ run }: { run: AgentResponse }) {
  const [open, setOpen] = useState(true)
  if (run.steps.length === 0) return null
  return (
    <div className="ml-11 rounded-xl border border-slate-200 bg-white text-sm">
      <button onClick={() => setOpen(o => !o)} aria-expanded={open} className="flex w-full items-center gap-2 px-3 py-2 text-left text-slate-600">
        <Wrench className="size-4" aria-hidden />
        <span className="font-medium">{run.steps.length} tool call{run.steps.length > 1 ? 's' : ''}</span>
        <span className="text-xs text-slate-400">· {run.iterations} reasoning step{run.iterations > 1 ? 's' : ''}</span>
        <ChevronDown className={cx('ml-auto size-4 transition', open && 'rotate-180')} aria-hidden />
      </button>
      {open && (
        <ol className="space-y-2 border-t border-slate-200 px-3 py-3">
          {run.steps.map((s, i) => (
            <li key={i} className="rounded-lg bg-slate-50 p-2.5">
              <div className="flex items-center gap-2">
                <code className="text-xs font-semibold text-slate-800">{s.tool}</code>
                <Badge tone={stepTone[s.status] ?? 'slate'}>{s.status}</Badge>
                <span className="ml-auto text-xs text-slate-400 tabular-nums">{s.latencyMs} ms</span>
              </div>
              <pre className="mt-1.5 overflow-x-auto text-[11px] text-slate-600">{JSON.stringify(s.arguments, null, 2)}</pre>
              {s.error && <p className="mt-1 text-xs text-amber-700">{s.error}</p>}
            </li>
          ))}
        </ol>
      )}
    </div>
  )
}
