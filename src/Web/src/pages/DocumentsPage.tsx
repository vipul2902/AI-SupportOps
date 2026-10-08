import { useRef, useState, type DragEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { FileText, RefreshCw, Trash2, Upload } from 'lucide-react'
import { api } from '../lib/api'
import { useAuth } from '../lib/auth'
import { bytes, timeAgo } from '../lib/format'
import { hasRole, type ChunkDto, type DocumentDto, type DocumentStatus, type Paged } from '../lib/types'
import { Badge, Button, Card, cx, Dialog, EmptyState, ErrorState, PageHeader, Spinner } from '../components/ui'
import { useToast } from '../components/toast'

const ACCEPT = '.pdf,.txt,.md,.markdown,.docx'
const statusTone: Record<DocumentStatus, 'slate' | 'blue' | 'green' | 'red'> = { Uploaded: 'slate', Processing: 'blue', Processed: 'green', Failed: 'red' }

export default function DocumentsPage() {
  const { me } = useAuth()
  const toast = useToast()
  const queryClient = useQueryClient()
  const fileInput = useRef<HTMLInputElement>(null)
  const [dragging, setDragging] = useState(false)
  const [viewing, setViewing] = useState<DocumentDto | null>(null)
  const canUpload = hasRole(me?.role, 'Agent')
  const canManage = hasRole(me?.role, 'Admin')

  const documents = useQuery({
    queryKey: ['documents'],
    queryFn: () => api<Paged<DocumentDto>>('/api/documents?pageSize=100'),
    // Poll only while something is being processed in the background.
    refetchInterval: q => (q.state.data?.items.some(d => d.status === 'Uploaded' || d.status === 'Processing') ? 2000 : false),
  })

  const upload = useMutation({
    mutationFn: async (file: File) => {
      const form = new FormData()
      form.append('file', file)
      return api<DocumentDto>('/api/documents', { method: 'POST', body: form })
    },
    onSuccess: doc => { toast.success(`${doc.fileName} uploaded. Processing…`); void queryClient.invalidateQueries({ queryKey: ['documents'] }) },
    onError: e => toast.error(e),
  })

  const reprocess = useMutation({
    mutationFn: (id: string) => api(`/api/documents/${id}/reprocess`, { method: 'POST' }),
    onSuccess: () => { toast.success('Queued for reprocessing'); void queryClient.invalidateQueries({ queryKey: ['documents'] }) },
    onError: e => toast.error(e),
  })

  const remove = useMutation({
    mutationFn: (id: string) => api(`/api/documents/${id}`, { method: 'DELETE' }),
    onSuccess: () => { toast.success('Document deleted'); void queryClient.invalidateQueries({ queryKey: ['documents'] }) },
    onError: e => toast.error(e),
  })

  function uploadFiles(files: FileList | null) {
    Array.from(files ?? []).forEach(f => upload.mutate(f))
  }

  function onDrop(e: DragEvent) {
    e.preventDefault()
    setDragging(false)
    if (canUpload) uploadFiles(e.dataTransfer.files)
  }

  return (
    <div className="mx-auto max-w-6xl p-6">
      <PageHeader title="Knowledge base" description="Documents the AI assistant answers from. Files are processed in the background into searchable chunks."
        actions={canUpload && <Button onClick={() => fileInput.current?.click()} loading={upload.isPending}><Upload className="size-4" aria-hidden />Upload</Button>} />
      <input ref={fileInput} type="file" accept={ACCEPT} multiple hidden onChange={e => { uploadFiles(e.target.files); e.target.value = '' }} data-testid="file-input" />

      {canUpload && (
        <div onDragOver={e => { e.preventDefault(); setDragging(true) }} onDragLeave={() => setDragging(false)} onDrop={onDrop}
          className={cx('mb-6 rounded-xl border-2 border-dashed px-6 py-8 text-center text-sm transition',
            dragging ? 'border-brand-500 bg-brand-50' : 'border-slate-300 bg-white text-slate-500')}>
          Drop PDF, DOCX, Markdown, or text files here (max 20 MB each)
        </div>
      )}

      <Card>
        {documents.isPending && <Spinner />}
        {documents.isError && <ErrorState error={documents.error} retry={() => void documents.refetch()} />}
        {documents.data?.items.length === 0 && (
          <EmptyState icon={<FileText className="size-6" />} title="No documents yet"
            description={canUpload ? 'Upload your help articles, policies, and guides to start answering questions.' : 'An agent or admin can upload documents.'} />
        )}
        {!!documents.data?.items.length && (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead className="border-b border-slate-200 text-left text-xs tracking-wide text-slate-500 uppercase">
                <tr><th className="px-4 py-3 font-medium">Name</th><th className="px-4 py-3 font-medium">Status</th><th className="px-4 py-3 font-medium">Chunks</th>
                  <th className="px-4 py-3 font-medium">Size</th><th className="px-4 py-3 font-medium">Uploaded</th><th className="px-4 py-3" /></tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {documents.data.items.map(d => (
                  <tr key={d.id} className="hover:bg-slate-50">
                    <td className="px-4 py-3">
                      <button className="flex items-center gap-2 text-left font-medium text-slate-900 hover:text-brand-700" onClick={() => setViewing(d)} disabled={d.status !== 'Processed'}>
                        <FileText className="size-4 text-slate-400" aria-hidden />{d.fileName}
                      </button>
                      {d.error && <p className="mt-0.5 text-xs text-red-600">{d.error}</p>}
                    </td>
                    <td className="px-4 py-3"><Badge tone={statusTone[d.status]}>{d.status}</Badge></td>
                    <td className="px-4 py-3 tabular-nums">{d.status === 'Processed' ? d.chunkCount : '—'}</td>
                    <td className="px-4 py-3 text-slate-500 tabular-nums">{bytes(d.sizeBytes)}</td>
                    <td className="px-4 py-3 text-slate-500">{timeAgo(d.createdAt)}</td>
                    <td className="px-4 py-3 text-right whitespace-nowrap">
                      {canManage && (d.status === 'Processed' || d.status === 'Failed') && (
                        <button onClick={() => reprocess.mutate(d.id)} aria-label={`Reprocess ${d.fileName}`} className="rounded p-1.5 text-slate-400 hover:bg-slate-100 hover:text-slate-700"><RefreshCw className="size-4" /></button>
                      )}
                      {canManage && (
                        <button onClick={() => { if (confirm(`Delete ${d.fileName}? This removes it from the knowledge base.`)) remove.mutate(d.id) }}
                          aria-label={`Delete ${d.fileName}`} className="rounded p-1.5 text-slate-400 hover:bg-red-50 hover:text-red-600"><Trash2 className="size-4" /></button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>

      {viewing && <ChunksDialog document={viewing} onClose={() => setViewing(null)} />}
    </div>
  )
}

function ChunksDialog({ document, onClose }: { document: DocumentDto; onClose: () => void }) {
  const chunks = useQuery({
    queryKey: ['chunks', document.id],
    queryFn: () => api<Paged<ChunkDto>>(`/api/documents/${document.id}/chunks?pageSize=100`),
  })
  return (
    <Dialog open title={`${document.fileName}: ${document.chunkCount} chunks`} onClose={onClose} footer={<Button variant="secondary" onClick={onClose}>Close</Button>}>
      <p className="text-xs text-slate-500">How this document was split for retrieval. Each chunk is embedded and searched independently.</p>
      <div className="max-h-[60vh] space-y-2 overflow-y-auto">
        {chunks.isPending && <Spinner />}
        {chunks.data?.items.map(c => (
          <div key={c.id} className="rounded-lg border border-slate-200 p-3">
            <p className="mb-1 text-xs text-slate-500">
              #{c.index + 1} · {c.tokenCount} tokens{c.pageNumber !== null && ` · page ${c.pageNumber}`}{c.heading && ` · ${c.heading}`}
            </p>
            <p className="text-sm whitespace-pre-wrap text-slate-700">{c.content}</p>
          </div>
        ))}
      </div>
    </Dialog>
  )
}
