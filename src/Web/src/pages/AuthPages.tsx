import { useState, type FormEvent, type ReactNode } from 'react'
import { Link, useLocation, useNavigate, useSearchParams } from 'react-router'
import { BarChart3 } from 'lucide-react'
import { useAuth } from '../lib/auth'
import { Button, Card, Field, Input } from '../components/ui'

function AuthLayout({ title, subtitle, children, footer }: { title: string; subtitle: string; children: ReactNode; footer?: ReactNode }) {
  return (
    <div className="flex min-h-full items-center justify-center px-4 py-12">
      <div className="w-full max-w-sm">
        <div className="mb-8 flex flex-col items-center text-center">
          <div className="mb-4 flex size-10 items-center justify-center rounded-xl bg-brand-600 text-white"><BarChart3 className="size-5" aria-hidden /></div>
          <h1 className="text-xl font-semibold tracking-tight">{title}</h1>
          <p className="mt-1 text-sm text-slate-500">{subtitle}</p>
        </div>
        <Card className="p-6">{children}</Card>
        {footer && <p className="mt-6 text-center text-sm text-slate-500">{footer}</p>}
      </div>
    </div>
  )
}

function FormError({ message }: { message: string | null }) {
  return message ? <p role="alert" className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{message}</p> : null
}

const errorMessage = (e: unknown) => (e instanceof Error ? e.message : 'Something went wrong.')

export function LoginPage() {
  const { login } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const form = new FormData(e.currentTarget)
    setBusy(true)
    setError(null)
    try {
      await login(String(form.get('email')), String(form.get('password')))
      navigate((location.state as { from?: string } | null)?.from ?? '/dashboard', { replace: true })
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <AuthLayout title="Sign in to AI-SupportOps" subtitle="AI-powered support and knowledge platform"
      footer={<>New here? <Link to="/register" className="font-medium text-brand-600 hover:underline">Create an organization</Link></>}>
      <form onSubmit={submit} className="space-y-4">
        <FormError message={error} />
        <Field label="Email" htmlFor="email"><Input id="email" name="email" type="email" autoComplete="email" required /></Field>
        <Field label="Password" htmlFor="password"><Input id="password" name="password" type="password" autoComplete="current-password" required /></Field>
        <Button type="submit" className="w-full" loading={busy}>Sign in</Button>
      </form>
    </AuthLayout>
  )
}

export function RegisterPage() {
  const { register } = useAuth()
  const navigate = useNavigate()
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const f = new FormData(e.currentTarget)
    setBusy(true)
    setError(null)
    try {
      await register({
        email: String(f.get('email')),
        password: String(f.get('password')),
        displayName: String(f.get('displayName')),
        organizationName: String(f.get('organizationName')),
      })
      navigate('/dashboard', { replace: true })
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <AuthLayout title="Create your organization" subtitle="You'll be its owner and can invite your team."
      footer={<>Already have an account? <Link to="/login" className="font-medium text-brand-600 hover:underline">Sign in</Link></>}>
      <form onSubmit={submit} className="space-y-4">
        <FormError message={error} />
        <Field label="Organization name" htmlFor="organizationName"><Input id="organizationName" name="organizationName" required maxLength={100} /></Field>
        <Field label="Your name" htmlFor="displayName"><Input id="displayName" name="displayName" autoComplete="name" required maxLength={100} /></Field>
        <Field label="Work email" htmlFor="email"><Input id="email" name="email" type="email" autoComplete="email" required /></Field>
        <Field label="Password" htmlFor="password" hint="At least 12 characters.">
          <Input id="password" name="password" type="password" autoComplete="new-password" required minLength={12} />
        </Field>
        <Button type="submit" className="w-full" loading={busy}>Create organization</Button>
      </form>
    </AuthLayout>
  )
}

export function AcceptInvitationPage() {
  const { acceptInvitation } = useAuth()
  const navigate = useNavigate()
  const [params] = useSearchParams()
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const token = params.get('token') ?? ''

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const f = new FormData(e.currentTarget)
    setBusy(true)
    setError(null)
    try {
      await acceptInvitation({ token: String(f.get('token')), password: String(f.get('password')), displayName: String(f.get('displayName') ?? '') || undefined })
      navigate('/dashboard', { replace: true })
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <AuthLayout title="Join your team" subtitle="New here? Choose a name and password. Already have an account? Enter your existing password.">
      <form onSubmit={submit} className="space-y-4">
        <FormError message={error} />
        <Field label="Invitation token" htmlFor="token"><Input id="token" name="token" defaultValue={token} required /></Field>
        <Field label="Your name" htmlFor="displayName" hint="Only needed for new accounts."><Input id="displayName" name="displayName" autoComplete="name" /></Field>
        <Field label="Password" htmlFor="password"><Input id="password" name="password" type="password" autoComplete="new-password" required minLength={12} /></Field>
        <Button type="submit" className="w-full" loading={busy}>Accept invitation</Button>
      </form>
    </AuthLayout>
  )
}
