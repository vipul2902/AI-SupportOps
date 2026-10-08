import { Link } from 'react-router'
import { useQuery } from '@tanstack/react-query'
import { Bot, FileText, FlaskConical, Ticket } from 'lucide-react'
import { api } from '../lib/api'
import { useAuth } from '../lib/auth'
import { compact, humanize, ms, percent } from '../lib/format'
import { fillDays } from '../lib/metrics'
import { hasRole, type AiMetrics, type DocumentDto, type Paged, type TicketDto } from '../lib/types'
import { BarChart, HorizontalBars } from '../components/charts'
import { Badge, Button, Card, EmptyState, ErrorState, PageHeader, Spinner, Stat } from '../components/ui'

export default function DashboardPage() {
  const { me } = useAuth()
  const isAdmin = hasRole(me?.role, 'Admin')
  return (
    <div className="mx-auto max-w-6xl space-y-6 p-6">
      <PageHeader title={`Welcome back, ${me?.displayName.split(' ')[0] ?? ''}`} description="What's happening in your support workspace."
        actions={<Link to="/chat"><Button><Bot className="size-4" aria-hidden />Ask the assistant</Button></Link>} />
      <WorkspaceSummary />
      {isAdmin ? <AiQuality /> : (
        <Card className="p-5 text-sm text-slate-600">AI quality metrics and evaluations are visible to admins.</Card>
      )}
    </div>
  )
}

function WorkspaceSummary() {
  const documents = useQuery({ queryKey: ['documents'], queryFn: () => api<Paged<DocumentDto>>('/api/documents?pageSize=100') })
  const open = useQuery({ queryKey: ['tickets', 'status=Open'], queryFn: () => api<Paged<TicketDto>>('/api/tickets?status=Open&pageSize=5') })
  const mine = useQuery({ queryKey: ['tickets', 'assignee=me'], queryFn: () => api<Paged<TicketDto>>('/api/tickets?assignee=me&pageSize=1') })
  const processed = documents.data?.items.filter(d => d.status === 'Processed').length

  return (
    <div className="grid gap-4 sm:grid-cols-3">
      <Stat label="Searchable documents" value={processed ?? '…'} hint={<Link to="/documents" className="text-brand-600 hover:underline">Manage knowledge base</Link>} />
      <Stat label="Open tickets" value={open.data?.totalCount ?? '…'} hint={<Link to="/tickets?status=Open" className="text-brand-600 hover:underline">View queue</Link>} />
      <Stat label="Assigned to me" value={mine.data?.totalCount ?? '…'} hint={<Link to="/tickets?assignee=me" className="text-brand-600 hover:underline">My tickets</Link>} />
    </div>
  )
}

function AiQuality() {
  const metrics = useQuery({ queryKey: ['metrics', 30], queryFn: () => api<AiMetrics>('/api/evaluations/metrics?days=30') })
  if (metrics.isPending) return <Card><Spinner label="Loading AI metrics" /></Card>
  if (metrics.isError) return <Card><ErrorState error={metrics.error} retry={() => void metrics.refetch()} /></Card>
  const m = metrics.data
  if (m.totalAnswers === 0) {
    return (
      <Card>
        <EmptyState icon={<Bot className="size-6" />} title="No AI activity in the last 30 days"
          description="Upload documents and ask the assistant a question. Quality metrics will appear here."
          action={<Link to="/documents"><Button variant="secondary"><FileText className="size-4" aria-hidden />Add documents</Button></Link>} />
      </Card>
    )
  }

  const daily = fillDays(m.daily, 30)
  const outcomes = Object.entries(m.outcomes).sort((a, b) => b[1] - a[1]).map(([label, value]) => ({ label: humanize(label), value }))

  return (
    <>
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <Stat label="AI answers (30 days)" value={compact(m.totalAnswers)} hint={`${compact(m.inputTokens + m.outputTokens)} tokens`} />
        <Stat label="Answer rate" value={percent(m.answerRate)} hint={`${percent(m.abstentionRate)} said "not in knowledge base"`} />
        <Stat label="p95 latency" value={ms(m.latency.p95Ms)} hint={`median ${ms(m.latency.p50Ms)}`} />
        <Stat label="Satisfaction" value={percent(m.satisfaction)} hint={`${m.helpfulFeedback} helpful · ${m.unhelpfulFeedback} not helpful`} />
      </div>

      <div className="grid gap-4 lg:grid-cols-3">
        <Card className="p-5 lg:col-span-2">
          <h2 className="mb-4 text-sm font-semibold">AI answers per day</h2>
          <BarChart label="AI answers per day, last 30 days" data={daily.map(d => ({ key: d.date, label: formatDay(d.date), value: d.questions }))} />
        </Card>
        <Card className="p-5">
          <h2 className="mb-4 text-sm font-semibold">Answer outcomes</h2>
          <HorizontalBars rows={outcomes} />
          {m.uncitedRate !== null && m.uncitedRate > 0.05 && (
            <p className="mt-4 text-xs text-amber-700">{percent(m.uncitedRate)} of answers cited no source: review recent conversations.</p>
          )}
        </Card>
      </div>

      <div className="grid gap-4 lg:grid-cols-3">
        <Card className="p-5 lg:col-span-2">
          <h2 className="mb-3 text-sm font-semibold">Agent tool health</h2>
          {m.tools.length === 0 ? <p className="text-sm text-slate-500">The AI agent hasn't called any tools yet.</p> : (
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead className="text-left text-xs text-slate-500"><tr>
                  <th className="py-2 font-medium">Tool</th><th className="py-2 text-right font-medium">Calls</th><th className="py-2 text-right font-medium">Succeeded</th>
                  <th className="py-2 text-right font-medium">Denied</th><th className="py-2 text-right font-medium">Invalid</th><th className="py-2 text-right font-medium">Avg latency</th>
                </tr></thead>
                <tbody className="divide-y divide-slate-100 tabular-nums">
                  {m.tools.map(t => (
                    <tr key={t.tool}>
                      <td className="py-2"><code className="text-xs">{t.tool}</code></td>
                      <td className="py-2 text-right">{t.calls}</td>
                      <td className="py-2 text-right">{t.succeeded}</td>
                      <td className="py-2 text-right">{t.denied > 0 ? <Badge tone="red">{t.denied}</Badge> : 0}</td>
                      <td className="py-2 text-right">{t.invalid > 0 ? <Badge tone="amber">{t.invalid}</Badge> : 0}</td>
                      <td className="py-2 text-right">{ms(t.averageLatencyMs)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </Card>
        <Card className="p-5">
          <h2 className="mb-3 flex items-center gap-2 text-sm font-semibold"><FlaskConical className="size-4 text-slate-400" aria-hidden />Latest evaluation</h2>
          {m.latestEvaluation ? (
            <dl className="space-y-2 text-sm">
              <Row label="Retrieval hit rate" value={percent(m.latestEvaluation.summary.retrievalHitRate)} />
              <Row label="Citation accuracy" value={percent(m.latestEvaluation.summary.citationAccuracy)} />
              <Row label="Abstention accuracy" value={percent(m.latestEvaluation.summary.abstentionAccuracy)} />
              <Row label="Groundedness (judge)" value={percent(m.latestEvaluation.summary.groundedness)} />
              <Link to="/evaluations" className="block pt-1 text-xs text-brand-600 hover:underline">View all runs</Link>
            </dl>
          ) : <p className="text-sm text-slate-500">No evaluation runs yet. <Link to="/evaluations" className="text-brand-600 hover:underline">Run one</Link>.</p>}
        </Card>
      </div>
      <p className="flex items-center gap-1.5 text-xs text-slate-400"><Ticket className="size-3.5" aria-hidden />Metrics cover the last 30 days and are computed from stored answers, feedback, and tool calls.</p>
    </>
  )
}

function Row({ label, value }: { label: string; value: string }) {
  return <div className="flex justify-between"><dt className="text-slate-500">{label}</dt><dd className="font-medium tabular-nums">{value}</dd></div>
}

const formatDay = (iso: string) => new Date(`${iso}T00:00:00Z`).toLocaleDateString(undefined, { month: 'short', day: 'numeric', timeZone: 'UTC' })
