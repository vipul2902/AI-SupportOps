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

### Architecture

```
Internet ──HTTPS──► Container Apps ingress ──► web (nginx + SPA)            [external]
                                                  │  /api  (Host: aiso-api)
                                                  ▼
                                               api (ASP.NET Core + ingestion worker) [internal only]
                                                  │ managed identity
          ┌───────────────┬──────────────┬────────┴────────┬──────────────────┐
          ▼               ▼              ▼                 ▼                  ▼
   PostgreSQL Flex   Azure Managed   Blob Storage      Key Vault        App Insights
   (pgvector)        Redis           (documents)       (secrets)        (OTel export)

Container Apps Job "migrate" (same API image, --migrate-only) runs before each rollout.
```

### Why each service

| Need | Service | Why this one |
|---|---|---|
| Run the containers | **Azure Container Apps** | Runs the existing images unchanged; managed HTTPS ingress, revisions, autoscaling, internal service discovery; no cluster to operate (AKS would be overkill for two services) |
| One-shot migrations | **Container Apps Job** | Same image, `--migrate-only`; deploy waits for success before rolling out |
| Database + vectors | **PostgreSQL Flexible Server** | Managed Postgres with the `vector` extension (allow-listed via `azure.extensions`): no separate vector database to operate |
| Cache / rate limits | **Azure Managed Redis** | Managed, TLS. Chosen over Azure Cache for Redis because, to my knowledge, Microsoft has announced that service's retirement in favour of Managed Redis. **Verify current guidance before deploying.** |
| Document files | **Blob Storage** | Replicas don't share a disk and revisions discard container filesystems; blobs are shared and durable. Shared keys disabled: access via managed identity only |
| Secrets | **Key Vault** (RBAC) | Container Apps reference secrets by URI through the managed identity; no secret values in app settings or the repo |
| Telemetry | **Application Insights** (workspace-based) | Receives the app's OpenTelemetry traces/metrics/logs via the Azure Monitor exporter (enabled by `APPLICATIONINSIGHTS_CONNECTION_STRING`) |
| Network | **VNet** with a delegated `/23` subnet | Gives the API a known CIDR to trust for `X-Forwarded-For`; foundation for private endpoints later |

Not used, deliberately: AKS (operational overhead), Azure Container Registry (CI already publishes
public images to GHCR), Front Door/WAF (worth adding for a real customer launch), Azure OpenAI
(supported by configuration via `Ai:OpenAI:Endpoint`, not required).

### Deploy

```bash
# 1. Provision (once, or when infrastructure changes). Use an immutable sha-<commit> tag from CI.
az group create -n rg-aisupportops -l <region>
az deployment group what-if -g rg-aisupportops -f infra/main.bicep -p infra/main.parameters.json \
  -p apiImage=ghcr.io/vipul2902/aisupportops-api:sha-XXXXXXX webImage=ghcr.io/vipul2902/aisupportops-web:sha-XXXXXXX \
     postgresAdminPassword="$(openssl rand -base64 32)" jwtSigningKey="$(openssl rand -base64 48)" openAiApiKey="<key>"
az deployment group create  ...same arguments...          # outputs webUrl

# 2. Allow GitHub Actions to deploy without stored passwords (OIDC):
./infra/setup-github-oidc.sh <subscription-id> rg-aisupportops
#    then add the printed variables to the GitHub "production" environment.

# 3. Each release: Actions > "Deploy (Azure)" > Run workflow > image_tag = sha-XXXXXXX
#    migrate job (must succeed) → update api → update web → smoke test (/healthz 200, /api/me 401)
```

The deploying identity needs **Owner** or **User Access Administrator** on the resource group for the
initial provisioning (it creates role assignments). The GitHub OIDC identity only gets Contributor.

### Cost

Billable resources. I have **not** verified current prices; use the
[Azure Pricing Calculator](https://azure.microsoft.com/pricing/calculator/) for your region. Main drivers:

- **API replica kept at `minReplicas: 1`** (1 vCPU / 2 GiB, always on: the ingestion worker runs inside
  it). The web app (0.25 vCPU) is also always on.
- **PostgreSQL Flexible Server** Burstable B1ms + 32 GB storage.
- **Azure Managed Redis** smallest tier (Balanced_B0).
- Usage-based and usually small at demo scale: Log Analytics/App Insights ingestion, Blob storage, Key Vault operations.
- **OpenAI usage** is billed separately by OpenAI per token.

To pause spending, delete the resource group: `az group delete -n rg-aisupportops`.

### Verification status (be honest about what is proven)

| Item | Status |
|---|---|
| Bicep: types, API versions, property names | Verified offline: Bicep 0.48.1 compiles 24 resources with zero warnings |
| Workflows and setup script | Verified: actionlint + shellcheck clean |
| Blob storage, Azure Monitor exporter wiring, nginx `Host`/`X-Forwarded-Proto` changes | Verified locally (Azurite tests; production compose stack) |
| PostgreSQL 17 availability in your region | **Not verified**: `what-if` / deploy will tell; set `postgresVersion` if needed |
| Azure Managed Redis API/SKU acceptance | **Not verified against Azure**: offline type-check only |
| `ForwardedHeaders:ForwardLimit=2` through ingress → nginx → ingress | **Not verified on Azure**: after deploying, confirm the API logs the real client IP (it drives per-IP rate limits) |
| End-to-end deployment | **Not performed**: requires an Azure subscription and creates billable resources |

### Hardening next

- Private endpoints / VNet integration for PostgreSQL, Redis, Storage, Key Vault (remove the public Postgres endpoint).
- Azure Front Door with WAF in front of the web app; custom domain + managed certificate.
- Entra ID authentication for PostgreSQL (no password) and Redis.
- Separate identities for the migration job (DDL rights) and the API (DML only).
