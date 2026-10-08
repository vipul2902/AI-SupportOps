# Security

## Authentication

| Piece | Design | Why |
|---|---|---|
| Passwords | PBKDF2-HMAC-SHA512 via ASP.NET Core Identity's `PasswordHasher` (salted, versioned) | Vetted implementation; deliberately slow to resist offline cracking |
| Access token | JWT, HS256, **15 min**, claims `sub`, `tid` (tenant), `role` | Stateless validation on every request; short life bounds stale roles |
| Refresh token | 256-bit random opaque string, **only SHA-256 hash stored**, 14 days | DB leak does not yield usable tokens |
| Rotation | Every refresh revokes the old token and issues a new one in the same *family* | Limits the window of a stolen token |
| Reuse detection | Presenting an already-rotated token revokes the whole family | Detects theft: attacker and victim can't both keep refreshing |
| Login failures | Same message and similar timing for unknown email vs. wrong password | Prevents account enumeration |
| Rate limiting | Fixed window per client IP on `/api/auth/*` (default 10/min) | Slows brute force and credential stuffing |

The signing key comes from configuration (`Auth:SigningKey`, ≥ 32 bytes) and is validated at startup; it is never stored in `appsettings*.json`.

## Authorization (RBAC)

Roles are hierarchical: `Owner > Admin > Agent > Viewer`. Endpoint policies name a *minimum* role (`RequireAuthorization(Policies.Admin)`).

Sensitive operations re-check the caller's role **in the database** rather than trusting the token claim, so demotions take effect immediately. Business rules live in `Domain/Tenants/RolePolicy.cs`:

- Admins cannot create, modify, or remove Owners.
- An organization must always keep at least one Owner.
- Removing a member revokes their refresh tokens for that organization.

## Tenant isolation

Defense in depth across three layers:

1. **API**: the tenant comes only from the signed `tid` claim, never from a URL or body parameter.
2. **Data access**: an EF Core global query filter on every `ITenantOwned` entity adds `WHERE tenant_id = @current`. With no tenant, the filter matches nothing (fail closed).
3. **Write guard**: `SaveChanges` rejects inserts, updates, and deletes whose `TenantId` differs from the active tenant.

Bypassing the filter requires an explicit `IgnoreQueryFilters()` call (easy to grep and review). Flows that legitimately act on a tenant other than the token's (sign-up, accepting an invitation) use an explicit `ITenantScope.Enter(tenantId)` block.

Another tenant's resources return **404, not 403**, so their existence is not revealed.

Phase 5 extends this to vector search: every similarity query is tenant-filtered in SQL.

## Known trade-offs

- A removed or demoted user's *access token* stays cryptographically valid for up to 15 minutes. Endpoints that matter re-check membership; a revocation list in Redis is a possible future improvement.
- The rate limiter is in-memory per instance; a distributed (Redis) limiter is needed when scaling out.
- Invitation tokens are returned in the API response until an email provider is integrated.
