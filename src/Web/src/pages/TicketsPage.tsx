import { useState, type FormEvent } from 'react'
import { useSearchParams } from 'react-router'
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Bot, Plus, Search, Ticket as TicketIcon, X } from 'lucide-react'
import { api, ApiError, json } from '../lib/api'
import { useAuth } from '../lib/auth'
import { humanize, timeAgo } from '../lib/format'
import { hasRole, type AuditEntry, type MemberDto, type Paged, type TicketDto, type TicketPriority, type TicketStatus } from '../lib/types'
import { Badge, Button, Card, cx, Dialog, EmptyState, ErrorState, Field, Input, PageHeader, Select, Spinner, Textarea } from '../components/ui'
import { useToast } from '../components/toast'

const STATUSES: TicketStatus[] = ['Open', 'InProgress', 'Resolved', 'Closed']
const PRIORITIES: TicketPriority[] = ['Low', 'Medium', 'High', 'Critical']
const statusTone: Record<TicketStatus, 'blue' | 'amber' | 'green' | 'slate'> = { Open: 'blue', InProgress: 'amber', Resolved: 'green', Closed: 'slate' }
const priorityTone: Record<TicketPriority, 'slate' | 'blue' | 'amber' | 'red'> = { Low: 'slate', Medium: 'blue', High: 'amber', Critical: 'red' }

export default function TicketsPage() {
  const { me } = useAuth()
  const [params, setParams] = useSearchParams()
  const [creating, setCreating] = useState(false)
  const [search, setSearch] = useState(params.get('search') ?? '')
  const selectedId = params.get('ticket')
  const canEdit = hasRole(me?.role, 'Agent')

  const filters = { status: params.get('status') ?? '', priority: params.get('priority') ?? '', assignee: params.get('assignee') ?? '', search: params.get('search') ?? '' }
  const query = new URLSearchParams(Object.entries(filters).filter(([, v]) => v)).toString()

  const tickets = useQuery({
    queryKey: ['tickets', query],
    queryFn: () => api<Paged<TicketDto>>(`/api/tickets?pageSize=50&${query}`),
    placeholderData: keepPreviousData, // keep the list visible while filters change
  })

  function setFilter(key: string, value: string) {
    const next = new URLSearchParams(params)
    if (value) next.set(key, value)
    else next.delete(key)
    setParams(next, { replace: true })
  }

  return (
    <div className="flex h-full">
      <div className="min-w-0 flex-1 overflow-y-auto p-6">
        <PageHeader title="Tickets" description="Support requests from your customers, created by your team or the AI agent."
          actions={canEdit && <Button onClick={() => setCreating(true)}><Plus className="size-4" aria-hidden />New ticket</Button>} />

        <div className="mb-4 flex flex-wrap items-center gap-2">
          <form className="relative min-w-56 flex-1" onSubmit={e => { e.preventDefault(); setFilter('search', search.trim()) }}>
            <Search className="pointer-events-none absolute top-2.5 left-3 size-4 text-slate-400" aria-hidden />
            <Input value={search} onChange={e => setSearch(e.target.value)} placeholder="Search title or #number" className="pl-9" aria-label="Search tickets" />
          </form>
          <Select value={filters.status} onChange={e => setFilter('status', e.target.value)} aria-label="Status" className="w-auto">
            <option value="">All statuses</option>
            {STATUSES.map(s => <option key={s} value={s}>{humanize(s)}</option>)}
          </Select>
          <Select value={filters.priority} onChange={e => setFilter('priority', e.target.value)} aria-label="Priority" className="w-auto">
            <option value="">All priorities</option>
            {PRIORITIES.map(p => <option key={p} value={p}>{p}</option>)}
          </Select>
          <Select value={filters.assignee} onChange={e => setFilter('assignee', e.target.value)} aria-label="Assignee" className="w-auto">
            <option value="">Anyone</option>
            <option value="me">Assigned to me</option>
            <option value="unassigned">Unassigned</option>
          </Select>
        </div>

        <Card>
          {tickets.isPending && <Spinner />}
          {tickets.isError && <ErrorState error={tickets.error} retry={() => void tickets.refetch()} />}
          {tickets.data?.items.length === 0 && (
            <EmptyState icon={<TicketIcon className="size-6" />} title={query ? 'No tickets match these filters' : 'No tickets yet'}
              description={query ? 'Try clearing a filter.' : 'Create one here, or ask the AI agent to create one from the assistant.'} />
          )}
          <ul className="divide-y divide-slate-100">
            {tickets.data?.items.map(t => (
              <li key={t.id}>
                <button onClick={() => setFilter('ticket', t.id)} className={cx('flex w-full items-center gap-3 px-4 py-3 text-left hover:bg-slate-50', t.id === selectedId && 'bg-brand-50/60')}>
                  <span className="w-12 shrink-0 text-xs font-medium text-slate-400 tabular-nums">#{t.number}</span>
                  <span className="min-w-0 flex-1">
                    <span className="flex items-center gap-1.5 truncate text-sm font-medium text-slate-900">
                      {t.source === 'AiAgent' && <Bot className="size-3.5 shrink-0 text-brand-600" aria-label="Created by AI agent" />}
                      {t.title}
                    </span>
                    <span className="block truncate text-xs text-slate-500">
                      {t.customer?.name ?? 'No customer'} · {t.assignee?.displayName ?? 'Unassigned'} · updated {timeAgo(t.updatedAt)}
                    </span>
                  </span>
                  <Badge tone={priorityTone[t.priority]}>{t.priority}</Badge>
                  <Badge tone={statusTone[t.status]}>{humanize(t.status)}</Badge>
                </button>
              </li>
            ))}
          </ul>
        </Card>
        {tickets.data && tickets.data.totalCount > tickets.data.items.length && (
          <p className="mt-2 text-xs text-slate-500">Showing {tickets.data.items.length} of {tickets.data.totalCount}. Refine filters to narrow down.</p>
        )}
      </div>

      {selectedId && <TicketPanel id={selectedId} canEdit={canEdit} onClose={() => setFilter('ticket', '')} />}
      <CreateTicketDialog open={creating} onClose={() => setCreating(false)} onCreated={id => { setCreating(false); setFilter('ticket', id) }} />
    </div>
  )
}

function TicketPanel({ id, canEdit, onClose }: { id: string; canEdit: boolean; onClose: () => void }) {
  const toast = useToast()
  const queryClient = useQueryClient()
  const ticket = useQuery({ queryKey: ['ticket', id], queryFn: () => api<TicketDto>(`/api/tickets/${id}`) })
  const history = useQuery({ queryKey: ['ticket-history', id], queryFn: () => api<AuditEntry[]>(`/api/tickets/${id}/history`) })
  const members = useQuery({ queryKey: ['members'], queryFn: () => api<MemberDto[]>('/api/team/members'), enabled: canEdit })

  const update = useMutation({
    // Sends the version we displayed: if someone changed the ticket meanwhile, the API returns 409.
    mutationFn: (changes: Record<string, unknown>) =>
      api<TicketDto>(`/api/tickets/${id}`, { method: 'PATCH', body: json({ ...changes, expectedVersion: ticket.data?.version }) }),
    onSuccess: updated => {
      queryClient.setQueryData(['ticket', id], updated)
      void queryClient.invalidateQueries({ queryKey: ['tickets'] })
      void queryClient.invalidateQueries({ queryKey: ['ticket-history', id] })
      toast.success('Ticket updated')
    },
    onError: e => {
      toast.error(e instanceof ApiError && e.status === 409 ? 'Someone else changed this ticket. Showing the latest version.' : e)
      void queryClient.invalidateQueries({ queryKey: ['ticket', id] })
    },
  })

  const t = ticket.data
  return (
    <aside className="flex w-full max-w-md shrink-0 flex-col border-l border-slate-200 bg-white" aria-label="Ticket details">
      <div className="flex items-center justify-between border-b border-slate-200 px-5 py-3">
        <span className="text-sm font-medium text-slate-500">{t ? `Ticket #${t.number}` : 'Ticket'}</span>
        <button onClick={onClose} aria-label="Close" className="rounded p-1 text-slate-400 hover:bg-slate-100"><X className="size-4" /></button>
      </div>
      <div className="min-h-0 flex-1 space-y-5 overflow-y-auto p-5">
        {ticket.isPending && <Spinner />}
        {ticket.isError && <ErrorState error={ticket.error} />}
        {t && (
          <>
            <div>
              <h2 className="text-lg font-semibold">{t.title}</h2>
              <div className="mt-2 flex flex-wrap gap-1.5">
                <Badge tone={statusTone[t.status]}>{humanize(t.status)}</Badge>
                <Badge tone={priorityTone[t.priority]}>{t.priority}</Badge>
                {t.source === 'AiAgent' && <Badge tone="brand"><Bot className="size-3" aria-hidden />Created by AI</Badge>}
              </div>
              {t.description && <p className="mt-3 text-sm whitespace-pre-wrap text-slate-700">{t.description}</p>}
            </div>

            <dl className="grid grid-cols-2 gap-x-4 gap-y-3 text-sm">
              <div><dt className="text-xs text-slate-500">Customer</dt><dd>{t.customer ? `${t.customer.name}` : '—'}</dd></div>
              <div><dt className="text-xs text-slate-500">Created</dt><dd>{timeAgo(t.createdAt)}</dd></div>
              <div className="col-span-2">
                <dt className="mb-1 text-xs text-slate-500">Assignee</dt>
                {canEdit ? (
                  <Select value={t.assignee?.id ?? ''} disabled={update.isPending} aria-label="Assignee"
                    onChange={e => update.mutate(e.target.value ? { assigneeUserId: e.target.value } : { unassign: true })}>
                    <option value="">Unassigned</option>
                    {members.data?.filter(m => m.role !== 'Viewer').map(m => <option key={m.userId} value={m.userId}>{m.displayName}</option>)}
                  </Select>
                ) : <dd>{t.assignee?.displayName ?? 'Unassigned'}</dd>}
              </div>
              {canEdit && (
                <div className="col-span-2">
                  <dt className="mb-1 text-xs text-slate-500">Priority</dt>
                  <Select value={t.priority} disabled={update.isPending} aria-label="Priority" onChange={e => update.mutate({ priority: e.target.value })}>
                    {PRIORITIES.map(p => <option key={p} value={p}>{p}</option>)}
                  </Select>
                </div>
              )}
            </dl>

            {canEdit && t.allowedNextStatuses.length > 0 && (
              <div>
                <p className="mb-2 text-xs text-slate-500">Move to</p>
                <div className="flex flex-wrap gap-2">
                  {t.allowedNextStatuses.map(s => (
                    <Button key={s} variant="secondary" disabled={update.isPending} onClick={() => update.mutate({ status: s })}>{humanize(s)}</Button>
                  ))}
                </div>
              </div>
            )}

            <div>
              <p className="mb-2 text-xs font-medium tracking-wide text-slate-500 uppercase">History</p>
              <ol className="space-y-3 border-l border-slate-200 pl-4">
                {history.data?.map(h => (
                  <li key={h.id} className="relative text-xs">
                    <span className={cx('absolute top-1 -left-[21px] size-2.5 rounded-full ring-2 ring-white', h.actorType === 'AiAgent' ? 'bg-brand-500' : 'bg-slate-400')} aria-hidden />
                    <p className="text-slate-700">
                      <span className="font-medium">{h.actorName ?? 'System'}</span>
                      {h.actorType === 'AiAgent' && <span className="text-brand-600"> via AI agent</span>}
                      <span className="text-slate-400"> · {timeAgo(h.createdAt)}</span>
                    </p>
                    <ul className="mt-0.5 text-slate-500">
                      {h.changes.filter(c => c.field !== 'description').map(c => (
                        <li key={c.field}>{humanize(c.field)}: {c.from !== null && <><span className="line-through">{c.from}</span> → </>}{c.to ?? '—'}</li>
                      ))}
                    </ul>
                  </li>
                ))}
              </ol>
            </div>
          </>
        )}
      </div>
    </aside>
  )
}

function CreateTicketDialog({ open, onClose, onCreated }: { open: boolean; onClose: () => void; onCreated: (id: string) => void }) {
  const toast = useToast()
  const queryClient = useQueryClient()
  const create = useMutation({
    mutationFn: (body: { title: string; description: string; priority: TicketPriority }) => api<TicketDto>('/api/tickets', { method: 'POST', body: json(body) }),
    onSuccess: t => { toast.success(`Ticket #${t.number} created`); void queryClient.invalidateQueries({ queryKey: ['tickets'] }); onCreated(t.id) },
    onError: e => toast.error(e),
  })

  function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const f = new FormData(e.currentTarget)
    create.mutate({ title: String(f.get('title')), description: String(f.get('description')), priority: f.get('priority') as TicketPriority })
  }

  return (
    <Dialog open={open} title="New ticket" onClose={onClose}>
      <form onSubmit={submit} className="space-y-4" id="create-ticket">
        <Field label="Title" htmlFor="title"><Input id="title" name="title" required maxLength={200} autoFocus /></Field>
        <Field label="Description" htmlFor="description"><Textarea id="description" name="description" rows={4} maxLength={10000} /></Field>
        <Field label="Priority" htmlFor="priority">
          <Select id="priority" name="priority" defaultValue="Medium">{PRIORITIES.map(p => <option key={p}>{p}</option>)}</Select>
        </Field>
        <div className="flex justify-end gap-2">
          <Button type="button" variant="secondary" onClick={onClose}>Cancel</Button>
          <Button type="submit" loading={create.isPending}>Create ticket</Button>
        </div>
      </form>
    </Dialog>
  )
}
