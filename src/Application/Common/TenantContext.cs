namespace AISupportOps.Application.Common;

/// <summary>
/// Lets a use case explicitly act on a specific tenant for a bounded block of code,
/// e.g. joining a tenant via invitation or creating a new tenant at sign-up, where the
/// caller's token (if any) points at a different tenant or none at all.
/// </summary>
public interface ITenantScope
{
    IDisposable Enter(Guid tenantId);
}

/// <summary>
/// The effective tenant for persistence: an explicit scope if one is active,
/// otherwise the tenant from the caller's access token.
/// </summary>
public sealed class TenantContext(ICurrentUser currentUser) : ITenantContext, ITenantScope
{
    private Guid? _override;

    public Guid? TenantId => _override ?? currentUser.TenantId;

    public IDisposable Enter(Guid tenantId)
    {
        if (_override is not null)
        {
            throw new InvalidOperationException("Nested tenant scopes are not supported.");
        }

        _override = tenantId;
        return new Exit(this);
    }

    private sealed class Exit(TenantContext owner) : IDisposable
    {
        public void Dispose() => owner._override = null;
    }
}
