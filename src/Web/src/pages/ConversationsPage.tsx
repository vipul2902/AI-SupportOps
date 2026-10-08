import { Link } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { MessagesSquare, Trash2 } from 'lucide-react'
import { api } from '../lib/api'
import { timeAgo } from '../lib/format'
import type { ConversationSummary, Paged } from '../lib/types'
import { Button, Card, EmptyState, ErrorState, PageHeader, Spinner } from '../components/ui'
import { useToast } from '../components/toast'

export default function ConversationsPage() {
  const toast = useToast()
  const queryClient = useQueryClient()
  const conversations = useQuery({ queryKey: ['conversations'], queryFn: () => api<Paged<ConversationSummary>>('/api/conversations?pageSize=50') })
  const remove = useMutation({
    mutationFn: (id: string) => api(`/api/conversations/${id}`, { method: 'DELETE' }),
    onSuccess: () => { toast.success('Conversation deleted'); void queryClient.invalidateQueries({ queryKey: ['conversations'] }) },
    onError: e => toast.error(e),
  })

  return (
    <div className="mx-auto max-w-4xl p-6">
      <PageHeader title="Conversations" description="Your conversations with the AI assistant. They are private to you."
        actions={<Link to="/chat"><Button>New conversation</Button></Link>} />
      <Card>
        {conversations.isPending && <Spinner />}
        {conversations.isError && <ErrorState error={conversations.error} retry={() => void conversations.refetch()} />}
        {conversations.data?.items.length === 0 && (
          <EmptyState icon={<MessagesSquare className="size-6" />} title="No conversations yet" description="Ask the assistant a question to get started."
            action={<Link to="/chat"><Button variant="secondary">Open assistant</Button></Link>} />
        )}
        <ul className="divide-y divide-slate-100">
          {conversations.data?.items.map(c => (
            <li key={c.id} className="flex items-center gap-3 px-4 py-3 hover:bg-slate-50">
              <Link to={`/chat/${c.id}`} className="min-w-0 flex-1">
                <p className="truncate text-sm font-medium text-slate-900">{c.title}</p>
                <p className="text-xs text-slate-500">Last message {timeAgo(c.lastMessageAt)}</p>
              </Link>
              <button onClick={() => { if (confirm('Delete this conversation?')) remove.mutate(c.id) }} aria-label={`Delete ${c.title}`}
                className="rounded p-1.5 text-slate-400 hover:bg-red-50 hover:text-red-600"><Trash2 className="size-4" /></button>
            </li>
          ))}
        </ul>
      </Card>
    </div>
  )
}
