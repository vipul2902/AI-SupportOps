import ReactMarkdown from 'react-markdown'
import type { Citation } from '../lib/types'

/**
 * Renders an assistant answer as Markdown, turning inline [n] markers into citation chips that link
 * to the source list. react-markdown does not render raw HTML, so model output cannot inject markup.
 */
export function AnswerMarkdown({ text, citations, messageKey }: { text: string; citations: Citation[]; messageKey: string }) {
  const cited = new Set(citations.map(c => c.number))
  const linked = text.replace(/\[(\d{1,3})\]/g, (match, n: string) => (cited.has(Number(n)) ? `[${n}](#cite-${messageKey}-${n})` : match))

  return (
    <div className="prose-chat text-sm text-slate-800">
      <ReactMarkdown
        components={{
          a: ({ href, children }) => {
            if (href?.startsWith(`#cite-${messageKey}-`)) {
              return (
                <a href={href} className="mx-0.5 inline-flex size-4.5 -translate-y-0.5 items-center justify-center rounded bg-brand-100 align-middle text-[10px] font-semibold text-brand-700 no-underline hover:bg-brand-500 hover:text-white"
                  aria-label={`Source ${String(children)}`}>
                  {children}
                </a>
              )
            }
            // External links from model output: never let them act on our origin.
            return <a href={href} target="_blank" rel="noopener noreferrer nofollow">{children}</a>
          },
        }}
      >
        {linked}
      </ReactMarkdown>
    </div>
  )
}

export function CitationList({ citations, messageKey }: { citations: Citation[]; messageKey: string }) {
  if (citations.length === 0) return null
  return (
    <div className="mt-3 space-y-1.5">
      <p className="text-xs font-medium tracking-wide text-slate-500 uppercase">Sources</p>
      {citations.map(c => (
        <div key={c.number} id={`cite-${messageKey}-${c.number}`} className="flex gap-2.5 rounded-lg border border-slate-200 bg-slate-50 p-2.5 text-xs target:ring-2 target:ring-brand-500">
          <span className="flex size-5 shrink-0 items-center justify-center rounded bg-brand-100 font-semibold text-brand-700">{c.number}</span>
          <div className="min-w-0">
            <p className="font-medium text-slate-800">
              {c.fileName}
              {c.pageNumber !== null && <span className="text-slate-500"> · page {c.pageNumber}</span>}
              {c.heading && <span className="text-slate-500"> · {c.heading}</span>}
              <span className="ml-1.5 text-slate-400 tabular-nums">relevance {c.score.toFixed(2)}</span>
            </p>
            <p className="mt-0.5 line-clamp-2 text-slate-600">{c.snippet}</p>
          </div>
        </div>
      ))}
    </div>
  )
}
