import { useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { CheckCircle2, FlaskConical, Upload, XCircle } from 'lucide-react'
import { api, json } from '../lib/api'
import { ms, percent, timeAgo } from '../lib/format'
import type { EvaluationCase, EvaluationCaseResult, EvaluationRun } from '../lib/types'
import { Badge, Button, Card, cx, EmptyState, ErrorState, PageHeader, Spinner, Stat } from '../components/ui'
import { useToast } from '../components/toast'

interface DatasetFile { name: string; cases: EvaluationCase[] }

export default function EvaluationsPage() {
  const toast = useToast()
  const queryClient = useQueryClient()
  const fileInput = useRef<HTMLInputElement>(null)
  const [selected, setSelected] = useState<string | null>(null)
  const [useJudge, setUseJudge] = useState(true)

  const runs = useQuery({ queryKey: ['evaluation-runs'], queryFn: () => api<EvaluationRun[]>('/api/evaluations/runs') })
  const detail = useQuery({
    queryKey: ['evaluation-run', selected],
    queryFn: () => api<{ run: EvaluationRun; results: EvaluationCaseResult[] }>(`/api/evaluations/runs/${selected}`),
    enabled: !!selected,
  })

  const run = useMutation({
    mutationFn: (dataset: DatasetFile) =>
      api<{ run: EvaluationRun }>('/api/evaluations/runs', { method: 'POST', body: json({ name: dataset.name, cases: dataset.cases, useLlmJudge: useJudge }) }),
    onSuccess: r => {
      toast.success(`Evaluation "${r.run.name}" finished`)
      void queryClient.invalidateQueries({ queryKey: ['evaluation-runs'] })
      void queryClient.invalidateQueries({ queryKey: ['metrics'] })
      setSelected(r.run.id)
    },
    onError: e => toast.error(e),
  })

  async function onFile(file: File | undefined) {
    if (!file) return
    try {
      const dataset = JSON.parse(await file.text()) as DatasetFile
      if (!dataset.name || !Array.isArray(dataset.cases)) throw new Error('Expected { "name": string, "cases": [...] }.')
      run.mutate(dataset)
    } catch (e) {
      toast.error(e instanceof SyntaxError ? 'That file is not valid JSON.' : e)
    }
  }

  return (
    <div className="mx-auto max-w-6xl space-y-6 p-6">
      <PageHeader title="Evaluations"
        description="Run a labelled question set through the live RAG pipeline and score retrieval, citations, abstention, and groundedness."
        actions={
          <>
            <label className="flex items-center gap-2 text-sm text-slate-600">
              <input type="checkbox" checked={useJudge} onChange={e => setUseJudge(e.target.checked)} className="rounded border-slate-300" />
              LLM judge
            </label>
            <Button onClick={() => fileInput.current?.click()} loading={run.isPending}><Upload className="size-4" aria-hidden />Run dataset</Button>
          </>
        } />
      <input ref={fileInput} type="file" accept="application/json,.json" hidden onChange={e => { void onFile(e.target.files?.[0]); e.target.value = '' }} />
      {run.isPending && <Card><Spinner label="Running evaluation (this calls the AI for every case)" /></Card>}

      <Card>
        {runs.isPending && <Spinner />}
        {runs.isError && <ErrorState error={runs.error} />}
        {runs.data?.length === 0 && (
          <EmptyState icon={<FlaskConical className="size-6" />} title="No evaluation runs yet"
            description="Upload a dataset JSON (see evaluation/dataset.json in the repository). Upload the matching documents first." />
        )}
        <ul className="divide-y divide-slate-100">
          {runs.data?.map(r => (
            <li key={r.id}>
              <button onClick={() => setSelected(r.id)} className={cx('flex w-full flex-wrap items-center gap-x-6 gap-y-1 px-4 py-3 text-left text-sm hover:bg-slate-50', r.id === selected && 'bg-brand-50/60')}>
                <span className="min-w-40 flex-1 font-medium">{r.name}<span className="block text-xs font-normal text-slate-500">{timeAgo(r.startedAt)} · {r.chatModel} · {r.promptId}</span></span>
                <Metric label="Hit@K" value={r.summary.retrievalHitRate} />
                <Metric label="Citations" value={r.summary.citationAccuracy} />
                <Metric label="Abstention" value={r.summary.abstentionAccuracy} />
                <Metric label="Grounded" value={r.summary.groundedness} />
              </button>
            </li>
          ))}
        </ul>
      </Card>

      {selected && detail.isPending && <Spinner />}
      {detail.data && <RunDetail run={detail.data.run} results={detail.data.results} />}

      <p className="text-xs text-slate-500">
        Scores are signals, not proof. Citation checks show a source was provided, not that it supports every claim; the LLM judge is itself a model and can be wrong.
      </p>
    </div>
  )
}

function Metric({ label, value }: { label: string; value: number | null }) {
  return <span className="w-24 text-xs text-slate-500">{label}<span className="block text-sm font-semibold text-slate-900 tabular-nums">{percent(value)}</span></span>
}

function RunDetail({ run, results }: { run: EvaluationRun; results: EvaluationCaseResult[] }) {
  const s = run.summary
  return (
    <div className="space-y-4">
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <Stat label="Retrieval hit rate" value={percent(s.retrievalHitRate)} hint={`MRR ${s.meanReciprocalRank?.toFixed(2) ?? '—'}`} />
        <Stat label="Citation accuracy" value={percent(s.citationAccuracy)} hint={`answer rate ${percent(s.answerRate)}`} />
        <Stat label="Abstention accuracy" value={percent(s.abstentionAccuracy)} hint={`key facts ${percent(s.keyFactCoverage)}`} />
        <Stat label="Groundedness" value={percent(s.groundedness)} hint={`avg ${ms(s.averageLatencyMs)} per case`} />
      </div>
      <Card>
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead className="border-b border-slate-200 text-left text-xs text-slate-500">
              <tr><th className="px-4 py-2 font-medium">Case</th><th className="px-4 py-2 font-medium">Outcome</th><th className="px-4 py-2 font-medium">Source rank</th>
                <th className="px-4 py-2 font-medium">Cited</th><th className="px-4 py-2 font-medium">Key facts</th><th className="px-4 py-2 font-medium">Answer</th></tr>
            </thead>
            <tbody className="divide-y divide-slate-100 align-top">
              {results.map(r => (
                <tr key={r.caseId}>
                  <td className="px-4 py-2.5">
                    <span className="flex items-center gap-1.5 font-medium">
                      {r.abstentionCorrect
                        ? <CheckCircle2 className="size-4 text-emerald-600" aria-label="Correct behaviour" />
                        : <XCircle className="size-4 text-red-600" aria-label="Wrong behaviour" />}
                      {r.caseId}
                    </span>
                    <span className="block text-xs text-slate-500">{r.question}</span>
                  </td>
                  <td className="px-4 py-2.5"><Badge tone={r.shouldAbstain ? 'slate' : 'blue'}>{r.outcome}</Badge>{r.shouldAbstain && <span className="block text-xs text-slate-400">should abstain</span>}</td>
                  <td className="px-4 py-2.5 tabular-nums">{r.expectedSourceRank ?? '—'}</td>
                  <td className="px-4 py-2.5">{r.citedExpectedSource === null ? '—' : r.citedExpectedSource ? 'Yes' : 'No'}</td>
                  <td className="px-4 py-2.5 tabular-nums">{percent(r.keyFactCoverage)}{r.missingKeyFacts.length > 0 && <span className="block text-xs text-amber-700">missing: {r.missingKeyFacts.join(', ')}</span>}</td>
                  <td className="max-w-md px-4 py-2.5 text-xs text-slate-600">{r.answer}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Card>
    </div>
  )
}
