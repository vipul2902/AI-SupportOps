import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Copy, UserPlus, X } from 'lucide-react'
import { api, json } from '../lib/api'
import { useAuth } from '../lib/auth'
import { timeAgo } from '../lib/format'
import { hasRole, type InvitationDto, type MemberDto, type TenantRole } from '../lib/types'
import { Badge, Button, Card, Dialog, ErrorState, Field, Input, PageHeader, Select, Spinner } from '../components/ui'
import { useToast } from '../components/toast'

const ROLES: TenantRole[] = ['Viewer', 'Agent', 'Admin', 'Owner']
const roleTone: Record<TenantRole, 'slate' | 'blue' | 'brand' | 'amber'> = { Viewer: 'slate', Agent: 'blue', Admin: 'brand', Owner: 'amber' }

export default function TeamPage() {
  const { me } = useAuth()
  const toast = useToast()
  const queryClient = useQueryClient()
  const [inviting, setInviting] = useState(false)
  const [createdLink, setCreatedLink] = useState<string | null>(null)
  const isAdmin = hasRole(me?.role, 'Admin')
  // Mirrors the server's rules for which roles an actor may grant (the API enforces them regardless).
  const assignable = ROLES.filter(r => me?.role === 'Owner' || (isAdmin && r !== 'Owner'))

  const members = useQuery({ queryKey: ['members'], queryFn: () => api<MemberDto[]>('/api/team/members') })
  const invitations = useQuery({ queryKey: ['invitations'], queryFn: () => api<InvitationDto[]>('/api/team/invitations'), enabled: isAdmin })

  const changeRole = useMutation({
    mutationFn: ({ userId, role }: { userId: string; role: TenantRole }) => api(`/api/team/members/${userId}/role`, { method: 'PATCH', body: json({ role }) }),
    onSuccess: () => { toast.success('Role updated'); void queryClient.invalidateQueries({ queryKey: ['members'] }) },
    onError: e => { toast.error(e); void queryClient.invalidateQueries({ queryKey: ['members'] }) },
  })
  const removeMember = useMutation({
    mutationFn: (userId: string) => api(`/api/team/members/${userId}`, { method: 'DELETE' }),
    onSuccess: () => { toast.success('Member removed'); void queryClient.invalidateQueries({ queryKey: ['members'] }) },
    onError: e => toast.error(e),
  })
  const revoke = useMutation({
    mutationFn: (id: string) => api(`/api/team/invitations/${id}`, { method: 'DELETE' }),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['invitations'] }),
    onError: e => toast.error(e),
  })
  const invite = useMutation({
    mutationFn: (body: { email: string; role: TenantRole }) => api<{ token: string }>('/api/team/invitations', { method: 'POST', body: json(body) }),
    onSuccess: r => {
      setInviting(false)
      setCreatedLink(`${window.location.origin}/accept-invite?token=${encodeURIComponent(r.token)}`)
      void queryClient.invalidateQueries({ queryKey: ['invitations'] })
    },
    onError: e => toast.error(e),
  })

  function submitInvite(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const f = new FormData(e.currentTarget)
    invite.mutate({ email: String(f.get('email')), role: f.get('role') as TenantRole })
  }

  return (
    <div className="mx-auto max-w-4xl space-y-6 p-6">
      <PageHeader title="Team" description="Members of this organization and what they can do."
        actions={isAdmin && <Button onClick={() => setInviting(true)}><UserPlus className="size-4" aria-hidden />Invite</Button>} />

      <Card>
        {members.isPending && <Spinner />}
        {members.isError && <ErrorState error={members.error} />}
        <ul className="divide-y divide-slate-100">
          {members.data?.map(m => {
            const isSelf = m.userId === me?.userId
            const editable = isAdmin && !isSelf && (me?.role === 'Owner' || m.role !== 'Owner')
            return (
              <li key={m.userId} className="flex items-center gap-3 px-4 py-3">
                <div className="min-w-0 flex-1">
                  <p className="truncate text-sm font-medium">{m.displayName}{isSelf && <span className="text-slate-400"> (you)</span>}</p>
                  <p className="truncate text-xs text-slate-500">{m.email} · joined {timeAgo(m.joinedAt)}</p>
                </div>
                {editable ? (
                  <Select value={m.role} className="w-32" aria-label={`Role for ${m.displayName}`} disabled={changeRole.isPending}
                    onChange={e => changeRole.mutate({ userId: m.userId, role: e.target.value as TenantRole })}>
                    {assignable.concat(assignable.includes(m.role) ? [] : [m.role]).map(r => <option key={r}>{r}</option>)}
                  </Select>
                ) : <Badge tone={roleTone[m.role]}>{m.role}</Badge>}
                {editable && (
                  <button onClick={() => { if (confirm(`Remove ${m.displayName}? Their sessions end immediately.`)) removeMember.mutate(m.userId) }}
                    aria-label={`Remove ${m.displayName}`} className="rounded p-1.5 text-slate-400 hover:bg-red-50 hover:text-red-600"><X className="size-4" /></button>
                )}
              </li>
            )
          })}
        </ul>
      </Card>

      {isAdmin && !!invitations.data?.length && (
        <Card>
          <h2 className="border-b border-slate-200 px-4 py-3 text-sm font-semibold">Pending invitations</h2>
          <ul className="divide-y divide-slate-100">
            {invitations.data.map(i => (
              <li key={i.id} className="flex items-center gap-3 px-4 py-3 text-sm">
                <span className="min-w-0 flex-1 truncate">{i.email}</span>
                <Badge tone={roleTone[i.role]}>{i.role}</Badge>
                <span className="text-xs text-slate-500">expires {timeAgo(i.expiresAt)}</span>
                <Button variant="ghost" onClick={() => revoke.mutate(i.id)}>Revoke</Button>
              </li>
            ))}
          </ul>
        </Card>
      )}

      <section className="rounded-xl border border-slate-200 bg-white p-4 text-sm text-slate-600">
        <h2 className="mb-2 font-semibold text-slate-900">Roles</h2>
        <ul className="space-y-1">
          <li><b>Viewer</b>: ask the assistant, read documents and tickets.</li>
          <li><b>Agent</b>: also upload documents and create or update tickets, including through the AI agent.</li>
          <li><b>Admin</b>: also manage members, invitations, documents, and evaluations.</li>
          <li><b>Owner</b>: full control, including other owners.</li>
        </ul>
      </section>

      <Dialog open={inviting} title="Invite a team member" onClose={() => setInviting(false)}>
        <form onSubmit={submitInvite} className="space-y-4">
          <Field label="Email" htmlFor="invite-email"><Input id="invite-email" name="email" type="email" required autoFocus /></Field>
          <Field label="Role" htmlFor="invite-role">
            <Select id="invite-role" name="role" defaultValue="Agent">{assignable.map(r => <option key={r}>{r}</option>)}</Select>
          </Field>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="secondary" onClick={() => setInviting(false)}>Cancel</Button>
            <Button type="submit" loading={invite.isPending}>Create invitation</Button>
          </div>
        </form>
      </Dialog>

      <Dialog open={!!createdLink} title="Invitation created" onClose={() => setCreatedLink(null)}
        footer={<Button onClick={() => setCreatedLink(null)}>Done</Button>}>
        <p className="text-sm text-slate-600">Send this link to the invitee. It works once and expires in 7 days. (Email delivery isn't configured yet.)</p>
        <div className="flex gap-2">
          <Input readOnly value={createdLink ?? ''} aria-label="Invitation link" onFocus={e => e.target.select()} />
          <Button variant="secondary" aria-label="Copy link" onClick={() => { void navigator.clipboard.writeText(createdLink ?? ''); toast.success('Link copied') }}>
            <Copy className="size-4" />
          </Button>
        </div>
      </Dialog>
    </div>
  )
}
