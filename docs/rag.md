# RAG Pipeline

Retrieval-Augmented Generation answers questions from the tenant's own documents instead of the
model's training data. It has two halves: **ingestion** (offline, per document) and **retrieval +
generation** (online, per question).

## 1. Ingestion (implemented)

```
Upload ──► documents (status = Uploaded) + file storage
              │
              ▼   DocumentIngestionWorker (background, in-process)
        claim: SELECT … FOR UPDATE SKIP LOCKED      ──► status = Processing (lease starts)
              │
        extract   PDF: PdfPig, one section per page
                  DOCX: OpenXML, heading styles → "## "
                  TXT/MD: UTF-8 decode
              │
        normalize NFKC, newlines, control chars, PDF hyphenation, whitespace
              │
        chunk     paragraphs → sentences → words, packed to 512 tokens, 64-token overlap
              │
        embed     batches of 64, text = "file > heading\n\ncontent" (contextual header)
              │
        store     document_chunks + vector(1536) (replaces previous chunks in one transaction)
              ▼
        status = Processed (chunk_count)  |  Failed (safe error message)
```

### Why a Postgres-backed queue

The document row *is* the job. `FOR UPDATE SKIP LOCKED` lets any number of workers (one per API
instance) claim different documents without coordination. No extra infrastructure, no dual-write
problem between a database and a broker. A broker (Azure Service Bus) becomes worthwhile when
ingestion moves into its own service or throughput needs exceed polling.

### Reliability

| Concern | Mechanism |
|---|---|
| Worker crashes mid-document | Lease: a `Processing` row with `updated_at` older than `Ingestion:Lease` (10 min) is re-claimed |
| Transient failure (storage, DB) | Released back to `Uploaded`; retried until `MaxAttempts` (3) |
| Poison document | After `MaxAttempts`, marked `Failed` instead of retrying forever |
| Bad file (corrupt, encrypted, image-only) | `DocumentExtractionException` → `Failed` immediately with an actionable message |
| Re-running a document | Idempotent: chunks are replaced in the same transaction that marks it `Processed` |
| Retry / re-index | `POST /api/documents/{id}/reprocess` (Admin) |
| Decompression bombs | Extracted text capped at `Ingestion:MaxExtractedCharacters` |

### Chunking choices

- **Token-based, not character-based.** Embedding models limit and bill by tokens. Counting uses
  `cl100k_base`, the encoding of OpenAI's `text-embedding-3` models.
- **~512 tokens.** Small enough that a chunk is about one topic (precise retrieval) and several
  fit in a prompt; large enough to carry the context needed to answer. Tunable via config.
- **Structure-aware splitting.** Breaking on paragraphs, then sentences, keeps chunks coherent;
  arbitrary fixed-size windows cut facts in half and produce noisier embeddings.
- **Overlap (64 tokens).** A fact straddling a boundary still appears whole in one chunk.
- **Metadata for citations.** PDF chunks never span pages, so each carries one `page_number`;
  Markdown/DOCX chunks carry their section `heading`.

### Known limitations

- No OCR: scanned PDFs fail with a clear message.
- PDF tables and multi-column layouts are extracted in reading order but lose structure.
- Headings are detected for Markdown and DOCX styles, not inferred from PDF font sizes.

## 2. Embeddings and vector search (implemented)

### Model and metric

| Choice | Value | Why |
|---|---|---|
| Model | `text-embedding-3-small` | Strong retrieval quality per dollar; `-large` is several times more expensive per token (see current [OpenAI pricing](https://openai.com/api/pricing/)) for modest gains on typical support content |
| Dimensions | 1536 (`vector(1536)`) | Model default; fixed by the schema. A different size needs a migration and full re-index |
| Metric | Cosine distance (`<=>`) | OpenAI vectors are unit-normalized, so cosine ranks identically to dot product and is model-agnostic |
| Index | HNSW (`vector_cosine_ops`, m=16, ef_construction=64) | Better recall/latency than IVFFlat, no training step, handles continuous inserts. Cost: memory and slower builds |

Every chunk records its `embedding_model`; search only compares vectors from the active model,
so switching models never mixes incompatible vector spaces (re-index via `/reprocess`).

**Contextual chunk headers.** The embedded text is `"{file} > {heading}\n\n{content}"` while the
stored content stays clean. A chunk that just says "click Reset" becomes findable for "reset my
password" because its section is "Account security".

### The query

```sql
SELECT ..., 1 - (c.embedding <=> @q) AS score
FROM document_chunks c JOIN documents d ON d.id = c.document_id
WHERE c.tenant_id = @tenant AND d.tenant_id = @tenant      -- explicit, never trusted to a filter alone
  AND d.status = 'Processed' AND c.embedding_model = @model
  AND (cardinality(@docIds) = 0 OR c.document_id = ANY(@docIds))
ORDER BY c.embedding <=> @q                                -- must match the index expression
LIMIT @k
```

Raw SQL is deliberate: the tenant predicate is visible and reviewable, and the `ORDER BY` matches
the HNSW index exactly. The tenant ID comes from the token-derived tenant context, never from input.

### Filtered vector search (the subtle part)

HNSW finds the `ef_search` nearest candidates **first** and applies `WHERE` **afterwards**. In a
shared index, a small tenant's chunks may not be among the global nearest candidates, so a naive
query can return fewer than `k` rows, or none. Mitigations used:

- `hnsw.iterative_scan = relaxed_order` (pgvector 0.8+): keep walking the graph until enough rows pass the filter.
- `hnsw.ef_search = 100` (default 40): a wider candidate list.
- The planner may skip HNSW entirely for a selective tenant filter, using the `(tenant_id, …)`
  B-tree and an exact sort. For small tenants that is both faster and exact.

At larger scale: per-tenant partial indexes for big tenants, or table partitioning by tenant.

### Search API

`POST /api/search` `{ "query", "topK" (1–20), "documentIds"?, "minScore"? }` → ranked chunks
with `fileName`, `pageNumber`, `heading`, `score`. POST keeps customer queries out of URLs and access logs.

### Providers

`Ai:Provider = OpenAI | Fake` via Microsoft.Extensions.AI (`IEmbeddingGenerator`), so Azure OpenAI is a configuration change.
`Fake` is a deterministic feature-hashing embedder: it matches shared words, not meaning. It is
used by tests and keyless local development. Production configuration fails at startup
if `Provider=OpenAI` and no key is set.

## 3. Answer generation (implemented)

`POST /api/ask` `{ "question", "documentIds"? }`

```
question ─► preprocess ─► retrieve top 6 (tenant-scoped) ─► drop score < 0.30
                                                    │
                              none left? ───────────┴──► "I couldn't find this…"  (LLM NOT called)
                                                    │
               numbered <source> blocks, best-first, ≤ 3000 tokens, delimiters escaped
                                                    │
     system: versioned prompt (rag-answer.v1)   user: sources + <question>
                                                    │
                                   LLM (temperature 0.1, ≤ 800 output tokens)
                                                    │
                parse [n] markers ─► citations (verified against provided sources)
                                                    │
     Answered | Declined | Uncited | NoRelevantSources   + tokens, timings, prompt id
```

### Design decisions

| Decision | Reason |
|---|---|
| Relevance threshold, abstain without calling the LLM | The cheapest hallucination is the one never generated. Also saves cost and latency on unanswerable questions |
| Token-budgeted context (3000) | Bounds cost and latency. More context is not always better: models under-use the middle of long prompts |
| Numbered sources and `[n]` citations | Users can verify claims; the system can check citations mechanically |
| Citation verification | `[7]` when only 5 sources were given is recorded as an invalid citation, a cheap hallucination signal |
| `Uncited` outcome | An answer citing nothing is flagged, not trusted |
| Low temperature | Factual, repeatable answers |
| Prompts in versioned files (`Application/Ai/Prompts/*.vN.md`) | Reviewed like code, traceable per answer (`promptId`), A/B-testable via evaluation |
| Per-user rate limit (30 req/min, token bucket) | LLM calls cost money; protects against runaway clients |
| Provider failure or timeout → `503` | Clients can retry; internals are never leaked |

### Prompt injection defenses

Documents are untrusted: anyone who can upload can try to instruct the model.

1. **Role separation.** Rules live in the system message; document text only in the user message.
2. **Explicit data framing.** The prompt states that `<source>` content is reference data and never instructions.
3. **Delimiter escaping.** `</source>`, `<source`, and `<question>` inside document text are escaped, so a document cannot close its block and inject a fake "system" section. Covered by unit and integration tests.
4. **Least privilege.** The answer endpoint has no tools; the model can only produce text. Tool calling (Phase 10) adds authorization outside the model.
5. **Tenant scoping before the prompt.** Another tenant's text can never be retrieved, so it can never be leaked.

These reduce risk; they do not make injection impossible. No prompt-level defense is complete,
which is why authority (tools, data access) is enforced in code, not in the prompt.

## 4. Conversational chat with streaming (implemented)

`POST /api/chat` `{ "message", "conversationId"?, "documentIds"? }` → `text/event-stream`

```
event: meta   data: {"conversationId":"…","userMessageId":"…","conversationTitle":"…"}
event: delta  data: {"text":"According "}          ← many, as tokens are generated
event: delta  data: {"text":"to the documentation…"}
event: done   data: {"messageId":"…","outcome":"Answered","citations":[…],"retrievalQuery":"…","inputTokens":…}
              (or)  event: error  data: {"message":"…","messageId":"…"}
```

### Per turn

1. **Begin** (before any byte is sent): validate, load the conversation (private to its creator: 404 for anyone else), load short-term history, persist the user message. Errors here are real `4xx` responses.
2. **Rewrite** the follow-up into a standalone question (only when there is history; one small LLM call at temperature 0). "Where is the link sent?" → "How do I reset my password? Where is the link sent?". Falls back to the raw message if the model is unavailable.
3. **Prepare** with the shared RAG pipeline (threshold, budget, escaping). Abstain without an LLM call if nothing is relevant.
4. **Stream** the answer: system prompt + history window + current turn's sources/question.
5. **Persist** the assistant message with citations (jsonb snapshot), model, prompt id, tokens, latency, and status: `Completed`, `Interrupted` (client disconnected, partial answer kept), or `Failed`.

### Why SSE (not WebSockets/SignalR)

Answers flow one way, server to client. SSE is plain HTTP: it works through proxies and load balancers, needs no hub or sticky sessions, and the request carries normal auth and rate limiting. SignalR earns its place for bidirectional or server-initiated pushes (e.g. live ticket updates), not for this.

### Short-term context strategy

- The last **10 messages**, capped at **2,000 tokens**, selected newest-first and sent oldest-first.
- Old `[n]` markers are stripped: they referenced that turn's sources and would confuse the current numbering.
- Sources are attached **only to the current turn**. Re-sending past sources would multiply cost each turn.
- Failed messages are excluded; a window never starts with an orphaned assistant reply.

This bounds per-turn cost no matter how long the conversation runs. Long-range memory (summaries, Redis) is Phase 8.

### Limitations

- Citation checks prove a cited source was *provided*, not that it *supports* the claim. Groundedness scoring comes in Phase 11.
- Single-turn only; conversation memory comes in Phase 7–8.
- The `Fake` chat model answers with the first sentence of source [1]. Real quality requires `Ai:Provider=OpenAI`.
