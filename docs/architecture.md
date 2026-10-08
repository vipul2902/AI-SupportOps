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

## Cross-cutting decisions (Phase 1)

- **Errors** — unhandled exceptions become RFC 7807 `ProblemDetails` with a `traceId`; stack traces are never returned.
- **Health** — `/health/live` (process) vs `/health/ready` (PostgreSQL + Redis). Orchestrators restart on liveness failure and stop routing traffic on readiness failure.
- **Time** — `TimeProvider` is injected so timestamps are testable.
- **IDs** — UUIDv7 (`Guid.CreateVersion7()`): globally unique but time-ordered, which keeps B-tree indexes compact.
- **Migrations** — auto-applied only in Development/tests; production runs them as an explicit deployment step.
- **Configuration** — no secrets in `appsettings*.json`; they come from `.env` (Docker), user-secrets (host), or Key Vault/Container App secrets (Azure).
