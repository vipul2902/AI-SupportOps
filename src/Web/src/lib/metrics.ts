import type { AiMetrics } from './types'

/** Days without activity are real zeros, not gaps: fill them so the time axis is honest. */
export function fillDays(points: AiMetrics['daily'], days: number, today = new Date()) {
  const byDate = new Map(points.map(p => [p.date, p]))
  return Array.from({ length: days }, (_, i) => {
    const d = new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), today.getUTCDate() - (days - 1 - i)))
    const key = d.toISOString().slice(0, 10)
    return byDate.get(key) ?? { date: key, questions: 0, answered: 0, averageLatencyMs: null }
  })
}
