import { useState } from 'react'
import { cx } from './ui'

/**
 * Single-series bar chart (one hue: brand indigo, validated against the surface). Each column is a
 * focusable hit target taller than its bar, with a value-first tooltip on hover and focus. The same
 * data is available as a screen-reader table, so the tooltip never gates information.
 */
export function BarChart({ data, label, format = String }: {
  data: { key: string; label: string; value: number }[]
  label: string
  format?: (v: number) => string
}) {
  const [active, setActive] = useState<number | null>(null)
  const max = Math.max(1, ...data.map(d => d.value))

  return (
    <figure aria-label={label}>
      <div className="relative flex h-40 items-end gap-0.5 border-b border-slate-200" onPointerLeave={() => setActive(null)}>
        {data.map((d, i) => (
          <button key={d.key} type="button" tabIndex={0}
            className="group relative flex h-full min-w-0 flex-1 items-end focus:outline-none"
            onPointerEnter={() => setActive(i)} onFocus={() => setActive(i)} onBlur={() => setActive(null)}
            aria-label={`${d.label}: ${format(d.value)}`}>
            <span
              className={cx('w-full rounded-t bg-brand-500 transition-colors', active === i && 'bg-brand-700', d.value === 0 && 'bg-transparent')}
              style={{ height: `${(d.value / max) * 100}%`, minHeight: d.value > 0 ? 2 : 0 }} />
            {active === i && (
              <span role="tooltip" className="pointer-events-none absolute bottom-full left-1/2 z-10 mb-1 -translate-x-1/2 rounded-md bg-slate-900 px-2 py-1 text-center text-xs whitespace-nowrap text-white shadow">
                <span className="block font-semibold tabular-nums">{format(d.value)}</span>
                <span className="block text-slate-300">{d.label}</span>
              </span>
            )}
          </button>
        ))}
      </div>
      {data.length > 1 && (
        <figcaption className="mt-1.5 flex justify-between text-xs text-slate-400" aria-hidden>
          <span>{data[0].label}</span><span>{data[data.length - 1].label}</span>
        </figcaption>
      )}
      <table className="sr-only">
        <caption>{label}</caption>
        <tbody>{data.map(d => <tr key={d.key}><th scope="row">{d.label}</th><td>{format(d.value)}</td></tr>)}</tbody>
      </table>
    </figure>
  )
}

/** Labeled horizontal bars for a few categories: label and value in text ink, the bar carries magnitude. */
export function HorizontalBars({ rows }: { rows: { label: string; value: number }[] }) {
  const max = Math.max(1, ...rows.map(r => r.value))
  return (
    <ul className="space-y-2.5">
      {rows.map(r => (
        <li key={r.label}>
          <div className="mb-1 flex justify-between text-xs">
            <span className="text-slate-600">{r.label}</span>
            <span className="font-medium text-slate-900 tabular-nums">{r.value}</span>
          </div>
          <div className="h-2 rounded-full bg-slate-100">
            <div className="h-2 rounded-full bg-brand-500" style={{ width: `${(r.value / max) * 100}%` }} />
          </div>
        </li>
      ))}
    </ul>
  )
}
