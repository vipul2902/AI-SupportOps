# Architecture

## Style: modular monolith

One deployable unit, organized by business capability (Identity, Tenants, Documents, Knowledge, Chat, Agents, Tickets, Evaluations). This gives simple deployment, local debugging, and transactional consistency, while keeping boundaries clean enough to extract a module (e.g. document ingestion) into its own service if scaling ever requires it.

## Layers and dependency rule

```
Api  ──►  Infrastructure  ──►  Application  ──►  Domain
```

| Layer | Owns | Must not reference |
|---|---|---|
| Domain | Entities, enums, invariants | Anything else |
| Application | Use cases, ports (interfaces), DTOs, validation | Infrastructure, Api |
| Infrastructure | EF Core, pgvector, Redis, AI clients, file storage | Api |
| Api | HTTP endpoints, auth, middleware, composition root | — |

The rule is enforced by tests in `tests/UnitTests/Architecture`.

## Data model

```
tenants ─┬─< tenant_memberships >── users ──< refresh_tokens
         ├─< invitations
         ├─< documents ──< document_chunks (embedding vector(1536), HNSW)
         ├─< conversations ──< messages (citations jsonb)
         ├─< customers ──< support_tickets (xmin version)
         ├── ticket_counters (last_number)
         └─< audit_logs (changes jsonb, append-only, no FK to users)
```

Every tenant-owned table carries `tenant_id`, leads its composite indexes with it, and is covered by
the EF global query filter and write guard.

## Support tickets

| Concern | Design |
|---|---|
| Workflow | Explicit state machine in the domain (`SupportTicket.CanTransition`): Open → InProgress/Resolved/Closed; InProgress → Open/Resolved; Resolved → InProgress/Closed; Closed → Open. Invalid → 422 listing allowed next states |
| Numbering | `INSERT … ON CONFLICT DO UPDATE … RETURNING` on `ticket_counters`: atomic, per-tenant row lock, no `MAX()+1` race. Gaps possible on rollback (like sequences) |
| Lost updates | PostgreSQL `xmin` as row version. Clients send `expectedVersion`; a stale value → 409 |
| Assignment | Assignee must be a member of the same tenant with role ≥ Agent |
| PATCH semantics | `null` = unchanged; removal is explicit (`unassign`, `unlinkCustomer`) |
| Audit | `AuditTrail` adds the entry to the same unit of work as the change: committed or rolled back together. Field-level from/to, actor (user, and `AiAgent` in Phase 10), trace id |
| Reuse | The AI agent's ticket tools (Phase 10) call the same `TicketService`, so the same rules apply to people and AI |

## Observability

OpenTelemetry for traces, metrics, and logs, exported over OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT`
is set: the **Aspire Dashboard** in Docker Compose (http://localhost:18888), Azure Monitor in production.

**Traces.** One request is one connected trace:

```
POST /api/ask                                  (ASP.NET Core)
└─ rag.ask            outcome, citations
   ├─ rag.prepare     top_k, retrieved, top_score, sources_in_context, context_tokens
   │  └─ vector.search   results, top_score, embed_ms
   │     ├─ embeddings text-embedding-3-small   (GenAI span: model, tokens, duration)
   │     └─ SELECT … ORDER BY embedding <=> …   (Npgsql span)
   └─ chat gpt-4o-mini                          (GenAI span: model, tokens, finish reason)
```

Also: `chat.turn`, `agent.run` → `agent.tool` (per call, with status), `ingestion.process`.

**Metrics** (`aisupportops.*`): retrieval and generation latency histograms, answers by outcome and
channel, tokens by direction and operation, agent tool calls by tool and status, tool latency,
ingestion results/latency/chunks, embedding-cache hit/miss, rate-limit rejections. Plus ASP.NET Core,
HttpClient, Npgsql, runtime, and GenAI metrics from the libraries.

**Rules.**
- Metric tags are low-cardinality only (outcome, tool, status, …), enforced by a test. Model-invented
  tool names are bucketed as `unknown`.
- No prompts, answers, documents, or questions in telemetry. Microsoft.Extensions.AI's
  `EnableSensitiveData` stays off; a test asserts a secret in a question never appears in any span.
- Health probes are excluded from traces.
- Error responses return the W3C **trace id**, so a reported error links straight to its trace.
- JSON console logs outside Development; logs carry TraceId/SpanId for correlation.

Not instrumented: Redis commands (the OpenTelemetry Redis instrumentation is prerelease-only).
Redis behaviour is covered by the cache and rate-limit metrics, and its latency is inside parent spans.

## Cross-cutting decisions (Phase 1)

- **Errors** — unhandled exceptions become RFC 7807 `ProblemDetails` with a `traceId`; stack traces are never returned.
- **Health** — `/health/live` (process) vs `/health/ready` (PostgreSQL + Redis). Orchestrators restart on liveness failure and stop routing traffic on readiness failure.
- **Time** — `TimeProvider` is injected so timestamps are testable.
- **IDs** — UUIDv7 (`Guid.CreateVersion7()`): globally unique but time-ordered, which keeps B-tree indexes compact.
- **Migrations** — auto-applied only in Development/tests; production runs them as an explicit deployment step.
- **Configuration** — no secrets in `appsettings*.json`; they come from `.env` (Docker), user-secrets (host), or Key Vault/Container App secrets (Azure).
