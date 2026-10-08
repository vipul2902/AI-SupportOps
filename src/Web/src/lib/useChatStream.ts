import { useCallback, useEffect, useRef, useState } from 'react'
import { apiFetch, json } from './api'
import { readEventStream } from './sse'
import type { ChatDone, ChatMeta } from './types'

export interface StreamingTurn {
  question: string
  text: string
  meta: ChatMeta | null
  done: ChatDone | null
  error: string | null
  streaming: boolean
}

/** Sends one chat turn and exposes the streamed answer as it arrives. */
export function useChatStream(onComplete: (meta: ChatMeta | null, done: ChatDone | null) => void) {
  const [turn, setTurn] = useState<StreamingTurn | null>(null)
  const abortRef = useRef<AbortController | null>(null)

  useEffect(() => () => abortRef.current?.abort(), [])

  const send = useCallback(async (message: string, conversationId?: string) => {
    abortRef.current?.abort()
    const controller = new AbortController()
    abortRef.current = controller
    setTurn({ question: message, text: '', meta: null, done: null, error: null, streaming: true })

    let meta: ChatMeta | null = null
    let done: ChatDone | null = null
    try {
      // apiFetch refreshes an expired token *before* the stream starts; validation errors arrive as normal 4xx.
      const response = await apiFetch('/api/chat', {
        method: 'POST',
        body: json({ message, conversationId }),
        headers: { Accept: 'text/event-stream' },
        signal: controller.signal,
      })

      for await (const event of readEventStream(response.body!, controller.signal)) {
        switch (event.type) {
          case 'meta':
            meta = event.data
            setTurn(t => t && { ...t, meta: event.data })
            break
          case 'delta':
            setTurn(t => t && { ...t, text: t.text + event.data.text })
            break
          case 'done':
            done = event.data
            setTurn(t => t && { ...t, done: event.data, streaming: false })
            break
          case 'error':
            setTurn(t => t && { ...t, error: event.data.message, streaming: false })
            break
        }
      }
    } catch (e) {
      if (!controller.signal.aborted) {
        setTurn(t => t && { ...t, error: e instanceof Error ? e.message : 'The answer could not be streamed.', streaming: false })
      }
    } finally {
      setTurn(t => t && { ...t, streaming: false })
      onComplete(meta, done)
    }
  }, [onComplete])

  /** Stops generation; the server keeps the partial answer as "Interrupted". */
  const stop = useCallback(() => abortRef.current?.abort(), [])
  const reset = useCallback(() => setTurn(null), [])

  return { turn, send, stop, reset }
}
