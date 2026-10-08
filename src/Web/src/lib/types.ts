// DTOs mirroring the API contracts (camelCase JSON, enums as strings).

export type TenantRole = 'Viewer' | 'Agent' | 'Admin' | 'Owner'
export const roleRank: Record<TenantRole, number> = { Viewer: 0, Agent: 1, Admin: 2, Owner: 3 }
export const hasRole = (role: TenantRole | undefined, minimum: TenantRole) =>
  role !== undefined && roleRank[role] >= roleRank[minimum]

export interface AuthResponse {
  accessToken: string
  accessTokenExpiresAt: string
  refreshToken: string
  refreshTokenExpiresAt: string
  tenantId: string
  role: TenantRole
}

export interface MembershipSummary { tenantId: string; tenantName: string; role: TenantRole }

export interface Me {
  userId: string
  email: string
  displayName: string
  tenantId: string
  role: TenantRole
  memberships: MembershipSummary[]
}

export interface Paged<T> { items: T[]; page: number; pageSize: number; totalCount: number }

export interface Problem { title?: string; detail?: string; status?: number; traceId?: string; errors?: Record<string, string[]> }

// ---- documents ----
export type DocumentStatus = 'Uploaded' | 'Processing' | 'Processed' | 'Failed'
export interface DocumentDto {
  id: string
  fileName: string
  kind: 'Pdf' | 'PlainText' | 'Markdown' | 'Docx'
  contentType: string
  sizeBytes: number
  status: DocumentStatus
  chunkCount: number
  error: string | null
  createdAt: string
  processedAt: string | null
}
export interface ChunkDto { id: string; index: number; content: string; tokenCount: number; pageNumber: number | null; heading: string | null }

// ---- chat ----
export type AnswerOutcome = 'Answered' | 'Declined' | 'Uncited' | 'NoRelevantSources'
export interface Citation {
  number: number
  documentId: string
  fileName: string
  pageNumber: number | null
  heading: string | null
  score: number
  snippet: string
}
export interface ConversationSummary { id: string; title: string; createdAt: string; lastMessageAt: string }
export interface MessageDto {
  id: string
  role: 'User' | 'Assistant'
  content: string
  status: 'Completed' | 'Interrupted' | 'Failed'
  outcome: string | null
  citations: Citation[]
  createdAt: string
}
export interface ConversationDetail { id: string; title: string; createdAt: string; summary: string | null; messages: MessageDto[] }

export interface ChatMeta { conversationId: string; userMessageId: string; conversationTitle: string }
export interface ChatDone {
  messageId: string
  outcome: AnswerOutcome
  citations: Citation[]
  invalidCitationNumbers: number[]
  retrievalQuery: string
  model: string | null
  inputTokens: number | null
  outputTokens: number | null
  latencyMs: number
}
export type ChatEvent =
  | { type: 'meta'; data: ChatMeta }
  | { type: 'delta'; data: { text: string } }
  | { type: 'done'; data: ChatDone }
  | { type: 'error'; data: { message: string; messageId: string | null } }

// ---- agent ----
export interface AgentStep { tool: string; arguments: unknown; status: string; error: string | null; latencyMs: number }
export interface AgentResponse {
  conversationId: string
  messageId: string
  runId: string
  answer: string
  outcome: 'Completed' | 'IterationLimit'
  steps: AgentStep[]
  toolsOffered: string[]
  iterations: number
}

// ---- tickets ----
export type TicketStatus = 'Open' | 'InProgress' | 'Resolved' | 'Closed'
export type TicketPriority = 'Low' | 'Medium' | 'High' | 'Critical'
export interface TicketDto {
  id: string
  number: number
  title: string
  description: string
  status: TicketStatus
  priority: TicketPriority
  source: 'Manual' | 'AiAgent'
  customer: { id: string; name: string; email: string } | null
  assignee: { id: string; displayName: string } | null
  createdAt: string
  updatedAt: string
  resolvedAt: string | null
  closedAt: string | null
  version: number
  allowedNextStatuses: TicketStatus[]
}
export interface AuditEntry {
  id: string
  action: string
  actorType: 'User' | 'AiAgent' | 'System'
  actorName: string | null
  changes: { field: string; from: string | null; to: string | null }[]
  createdAt: string
}
export interface CustomerDto { id: string; name: string; email: string; company: string | null; createdAt: string }

// ---- team ----
export interface MemberDto { userId: string; email: string; displayName: string; role: TenantRole; joinedAt: string }
export interface InvitationDto { id: string; email: string; role: TenantRole; expiresAt: string; createdAt: string }
export interface TenantDto { id: string; name: string; slug: string; createdAt: string }

// ---- evaluation ----
export interface EvaluationSummary {
  caseCount: number
  retrievalHitRate: number | null
  meanReciprocalRank: number | null
  answerRate: number | null
  citationAccuracy: number | null
  keyFactCoverage: number | null
  abstentionAccuracy: number | null
  groundedness: number | null
  averageLatencyMs: number
  inputTokens: number
  outputTokens: number
}
export interface EvaluationRun {
  id: string
  name: string
  promptId: string
  chatModel: string
  embeddingModel: string
  usedJudge: boolean
  startedAt: string
  completedAt: string | null
  summary: EvaluationSummary
}
export interface EvaluationCaseResult {
  caseId: string
  question: string
  expectedSource: string | null
  shouldAbstain: boolean
  answer: string
  outcome: string
  expectedSourceRank: number | null
  citedExpectedSource: boolean | null
  keyFactCoverage: number | null
  missingKeyFacts: string[]
  abstentionCorrect: boolean
  groundedness: number | null
  latencyMs: number
}
export interface EvaluationCase { id: string; question: string; expectedSource?: string; keyFacts?: string[]; shouldAbstain?: boolean }

export interface AiMetrics {
  since: string
  totalAnswers: number
  outcomes: Record<string, number>
  answerRate: number | null
  abstentionRate: number | null
  uncitedRate: number | null
  averageCitationsPerAnswer: number | null
  averageTopCitationScore: number | null
  latency: { averageMs: number | null; p50Ms: number | null; p95Ms: number | null }
  inputTokens: number
  outputTokens: number
  helpfulFeedback: number
  unhelpfulFeedback: number
  satisfaction: number | null
  daily: { date: string; questions: number; answered: number; averageLatencyMs: number | null }[]
  tools: { tool: string; calls: number; succeeded: number; denied: number; invalid: number; failed: number; averageLatencyMs: number }[]
  latestEvaluation: EvaluationRun | null
}
