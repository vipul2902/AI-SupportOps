# Deployment

## Environments at a glance

| | Development (`docker-compose.yml`) | Production-style (`docker-compose.prod.yml`) |
|---|---|---|
| Entry point | API on :8080, Vite dev server on :5173 | nginx (`web`) only, `WEB_PORT` (default 80) |
| Postgres / Redis | Published on host ports | Internal network only, no host ports, no internet egress |
| Redis auth | None | `--requirepass`, cache-only (no persistence) |
| Migrations | Auto-applied at API startup | One-shot `migrate` job; API starts only after it exits 0 |
| ASP.NET environment | Development | Production (JSON logs, fail-fast config checks) |
| Observability | Aspire Dashboard on :18888 | `OTEL_EXPORTER_OTLP_ENDPOINT` (collector / Azure Monitor) |

## Images

**API** (`src/Api/Dockerfile`): SDK build stage → `aspnet:10.0-alpine` runtime. Restore is a separate
cached layer. Runs as the non-root `app` user (uid 1654). `HEALTHCHECK` hits `/health/live`. The same
image runs the migration job with `--migrate-only`.

**Web** (`src/Web/Dockerfile`): `node:24-alpine` build → `nginx-unprivileged` (uid 101, port 8080).
nginx (`src/Web/nginx/default.conf.template`):

- serves the SPA with an SPA fallback (`/chat`, `/tickets/…` → `index.html`);
- **`/api/` reverse proxy** with `proxy_buffering off` so Server-Sent Events stream token by token,
  `X-Forwarded-For`/`-Proto` set for the API, 300 s read timeout, 21 MB upload limit;
- caching: content-hashed `/assets/*` → `max-age=31536000, immutable`; `index.html` → `no-cache`, so
  a deployment is picked up immediately;
- security headers including a strict CSP (`default-src 'self'`, no inline script or style, `frame-ancestors 'none'`).

Same origin for SPA and API: no CORS, and the refresh cookie stays first-party.

## Run the production stack locally

```bash
cp .env.prod.example .env.prod          # set real secrets: openssl rand -base64 48
docker compose -f docker-compose.prod.yml --env-file .env.prod up -d --build
docker compose -f docker-compose.prod.yml --env-file .env.prod ps     # migrate: Exited (0), others healthy
```

Open `http://localhost:${WEB_PORT}`. `AI_PROVIDER=Fake` gives a keyless demo; use `OpenAI` with
`OPENAI_API_KEY` for real answers. Note: with the offline Fake embedder, the production relevance
threshold (`Rag:MinScore=0.30`, calibrated for real embeddings) makes the assistant abstain more often.

## Container hardening

| Control | Where |
|---|---|
| Non-root users | api 1654, web 101, redis 999 (runs as redis directly: no root-then-drop step) |
| Read-only root filesystem | api, migrate, web, redis; writable only `uploads` volume and `tmpfs` (`/tmp`, nginx config dir owned by uid 101) |
| `cap_drop: ALL` + `no-new-privileges` | all services; Postgres adds back only what its entrypoint needs to initialise the data directory |
| Resource limits | CPU/memory per service |
| Network segmentation | `backend` is `internal: true` (Postgres, Redis: no internet). `api` joins `frontend` too (it must reach the AI provider). Only `web` publishes a port |
| Trusted proxy | `ForwardedHeaders__KnownNetworks__0` = the `frontend` subnet; the API refuses to start in Production without it |

Verified (Phase 15): only `web` publishes a port; containers run as 1654/101/999; the API's root
filesystem is read-only; Postgres and Redis cannot resolve external hosts; the API records the real
client IP (`172.31.10.1`), not nginx's (`172.31.10.3`); register → cookie refresh → upload →
streamed answer all work through nginx.

## Migrations

Production never migrates on API startup: with several replicas, they would race. The `migrate`
service runs `dotnet AISupportOps.Api.dll --migrate-only` once, and `api` has
`depends_on: migrate: condition: service_completed_successfully`. Migrations are idempotent (a
re-run with nothing pending exits 0). In Azure the same image runs as a Container Apps **Job**
before the new revision receives traffic (Phase 17).

## TLS

Terminate HTTPS in front of `web` (cloud load balancer or ingress). The refresh cookie is `Secure`, so
browsers send it only over HTTPS; `http://localhost` is the only exception, which is why the local
production stack works over plain HTTP.

## Azure

_Phase 17._
