# AI-SupportOps

**AI-powered customer support and knowledge platform** — a multi-tenant SaaS where companies upload their documentation, get grounded AI answers with citations, and let a controlled AI agent work support tickets through audited tools.

> 🚧 Under active development. Built phase by phase; see [Roadmap](#roadmap).

## Why

Support teams answer the same questions repeatedly from knowledge that already exists in docs. AI-SupportOps makes that knowledge searchable (RAG over pgvector), answers with sources, and lets an AI agent take *approved* actions — with strict tenant isolation and auditability.

## Architecture

Modular monolith with Clean Architecture layering — one deployable, clear module boundaries, easy to split later if needed.

```
src/
  Domain/          Entities, invariants — no dependencies
  Application/     Use cases, interfaces (IRagService, IToolExecutor, ...)
  Infrastructure/  EF Core + PostgreSQL/pgvector, Redis, AI providers, storage
  Api/             ASP.NET Core endpoints, auth, middleware
  Web/             React + TypeScript (coming)
tests/
  UnitTests/         Fast tests + architecture rules
  IntegrationTests/  Real PostgreSQL + Redis via Testcontainers
```

Details: [docs/architecture.md](docs/architecture.md)

## Tech stack

| Area | Choice |
|---|---|
| Backend | .NET 10, ASP.NET Core, EF Core 10 |
| Data | PostgreSQL 17 + pgvector, Redis 7 |
| AI | OpenAI / Azure OpenAI (embeddings, chat, tool calling) |
| Frontend | React, TypeScript, Vite, TanStack Query |
| Testing | xUnit, Testcontainers |
| Infra | Docker Compose, GitHub Actions, Azure Container Apps |

## Local setup

Prerequisites: .NET 10 SDK, Docker Desktop.

```bash
cp .env.example .env              # set POSTGRES_PASSWORD, JWT_SIGNING_KEY (and ports if 5432/6379 are taken)

# Option A — everything in containers
docker compose --profile full up -d --build
curl http://localhost:8080/health/ready   # -> Healthy

# Option B — dependencies in Docker, API on the host (for debugging)
docker compose up -d
dotnet user-secrets --project src/Api set "ConnectionStrings:Postgres" \
  "Host=localhost;Port=5432;Database=aisupportops;Username=aisupportops;Password=<from .env>"
dotnet user-secrets --project src/Api set "ConnectionStrings:Redis" "localhost:6379"
dotnet user-secrets --project src/Api set "Auth:SigningKey" "$(openssl rand -base64 48)"
dotnet run --project src/Api
```

In Development the API applies EF Core migrations on startup.

### Environment variables

See [.env.example](.env.example). Secrets are never committed: Docker reads `.env` (git-ignored), host runs use `dotnet user-secrets`.

## Testing

```bash
dotnet test                       # integration tests need Docker running
```

## API overview

| Endpoint | Access |
|---|---|
| `POST /api/auth/register` | Anonymous: creates user + organization (caller becomes Owner) |
| `POST /api/auth/login`, `/refresh`, `/logout` | Anonymous (rate limited) |
| `POST /api/auth/accept-invitation` | Anonymous, requires invitation token |
| `POST /api/auth/switch-tenant` | Any member |
| `GET /api/me` | Any member |
| `GET /api/tenant` / `PATCH /api/tenant` | Viewer+ / Admin+ |
| `GET /api/team/members` | Viewer+ |
| `PATCH /api/team/members/{id}/role`, `DELETE /api/team/members/{id}` | Admin+ |
| `GET/POST /api/team/invitations`, `DELETE /api/team/invitations/{id}` | Admin+ |

Auth model, RBAC rules, and tenant isolation design: [docs/security.md](docs/security.md)

## Health endpoints

| Endpoint | Meaning |
|---|---|
| `GET /health/live` | Process is running |
| `GET /health/ready` | PostgreSQL and Redis are reachable |

## Roadmap

- [x] Phase 1 — Solution, Docker, PostgreSQL + pgvector, Redis, health checks
- [ ] Phase 2 — Authentication, organizations, multi-tenancy, RBAC
- [ ] Phase 3–4 — Document upload and ingestion pipeline
- [ ] Phase 5–6 — Embeddings, vector search, RAG
- [ ] Phase 7–8 — Streaming chat, citations, conversation memory
- [ ] Phase 9–10 — Support tickets, agent tool calling
- [ ] Phase 11–12 — AI evaluation, observability
- [ ] Phase 13+ — React dashboard, CI/CD, Azure deployment

## License

[MIT](LICENSE)
