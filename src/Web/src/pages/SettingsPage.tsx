import { type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, json } from '../lib/api'
import { useAuth } from '../lib/auth'
import { hasRole, type TenantDto } from '../lib/types'
import { Badge, Button, Card, Field, Input, PageHeader, Spinner } from '../components/ui'
import { useToast } from '../components/toast'

export default function SettingsPage() {
  const { me, switchTenant } = useAuth()
  const toast = useToast()
  const queryClient = useQueryClient()
  const tenant = useQuery({ queryKey: ['tenant'], queryFn: () => api<TenantDto>('/api/tenant') })
  const isAdmin = hasRole(me?.role, 'Admin')

  const rename = useMutation({
    mutationFn: (name: string) => api<TenantDto>('/api/tenant', { method: 'PATCH', body: json({ name }) }),
    onSuccess: async () => {
      toast.success('Organization renamed')
      await queryClient.invalidateQueries({ queryKey: ['tenant'] })
      if (me) await switchTenant(me.tenantId) // refresh the name shown in the switcher
    },
    onError: e => toast.error(e),
  })

  function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    rename.mutate(String(new FormData(e.currentTarget).get('name')).trim())
  }

  if (!me) return null
  return (
    <div className="mx-auto max-w-3xl space-y-6 p-6">
      <PageHeader title="Settings" />

      <Card className="p-5">
        <h2 className="mb-4 text-sm font-semibold">Organization</h2>
        {tenant.isPending ? <Spinner /> : tenant.data && (
          <form onSubmit={submit} className="space-y-4">
            <Field label="Name" htmlFor="org-name">
              <Input id="org-name" name="name" defaultValue={tenant.data.name} required maxLength={100} disabled={!isAdmin} />
            </Field>
            <p className="text-xs text-slate-500">Workspace id: <code>{tenant.data.slug}</code></p>
            {isAdmin && <Button type="submit" loading={rename.isPending}>Save</Button>}
          </form>
        )}
      </Card>

      <Card className="p-5">
        <h2 className="mb-4 text-sm font-semibold">Your profile</h2>
        <dl className="grid gap-3 text-sm sm:grid-cols-2">
          <div><dt className="text-xs text-slate-500">Name</dt><dd>{me.displayName}</dd></div>
          <div><dt className="text-xs text-slate-500">Email</dt><dd>{me.email}</dd></div>
          <div><dt className="text-xs text-slate-500">Role here</dt><dd><Badge tone="brand">{me.role}</Badge></dd></div>
        </dl>
      </Card>

      {me.memberships.length > 1 && (
        <Card className="p-5">
          <h2 className="mb-3 text-sm font-semibold">Your organizations</h2>
          <ul className="divide-y divide-slate-100">
            {me.memberships.map(m => (
              <li key={m.tenantId} className="flex items-center justify-between py-2.5 text-sm">
                <span>{m.tenantName} <span className="text-xs text-slate-400">· {m.role}</span></span>
                {m.tenantId === me.tenantId
                  ? <Badge tone="green">Current</Badge>
                  : <Button variant="secondary" onClick={() => void switchTenant(m.tenantId).catch(toast.error)}>Switch</Button>}
              </li>
            ))}
          </ul>
        </Card>
      )}
    </div>
  )
}
