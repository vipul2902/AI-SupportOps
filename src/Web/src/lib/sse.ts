import type { ChatEvent } from './types'

/**
 * Parses a text/event-stream body into events. EventSource cannot send a POST body or an
 * Authorization header, so the chat endpoint is read with fetch and parsed here.
 * Handles events split across network chunks and CRLF line endings.
 */
export async function* readEventStream(body: ReadableStream<Uint8Array>, signal?: AbortSignal): AsyncGenerator<ChatEvent> {
  const reader = body.getReader()
  // stream: true keeps a multi-byte UTF-8 character that is split across network chunks intact.
  const decoder = new TextDecoder()
  let buffer = ''
  try {
    while (true) {
      if (signal?.aborted) return
      const { value, done } = await reader.read()
      if (done) break
      buffer += decoder.decode(value, { stream: true }).replace(/\r\n/g, '\n')

      let boundary: number
      while ((boundary = buffer.indexOf('\n\n')) >= 0) {
        const event = parseEvent(buffer.slice(0, boundary))
        buffer = buffer.slice(boundary + 2)
        if (event) yield event
      }
    }
    const tail = parseEvent(buffer)
    if (tail) yield tail
  } finally {
    reader.releaseLock()
  }
}

export function parseEvent(block: string): ChatEvent | null {
  let type = 'message'
  const data: string[] = []
  for (const line of block.split('\n')) {
    if (line.startsWith('event:')) type = line.slice(6).trim()
    else if (line.startsWith('data:')) data.push(line.slice(5).replace(/^ /, ''))
  }
  if (data.length === 0) return null
  try {
    return { type, data: JSON.parse(data.join('\n')) } as ChatEvent
  } catch {
    return null
  }
}
