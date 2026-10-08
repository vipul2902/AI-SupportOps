using AISupportOps.Application.Common;
using AISupportOps.Domain.Tenants;

namespace AISupportOps.UnitTests.Identity;

public class TenantContextTests
{
    private static readonly Guid TokenTenant = Guid.NewGuid();

    [Fact]
    public void Uses_token_tenant_by_default_and_scope_overrides_only_inside_block()
    {
        var context = new TenantContext(new StubUser(TokenTenant));
        var other = Guid.NewGuid();

        Assert.Equal(TokenTenant, context.TenantId);
        using (context.Enter(other))
        {
            Assert.Equal(other, context.TenantId);
        }

        Assert.Equal(TokenTenant, context.TenantId);
    }

    [Fact]
    public void Nested_scopes_are_rejected()
    {
        var context = new TenantContext(new StubUser(null));
        using var outer = context.Enter(Guid.NewGuid());

        Assert.Throws<InvalidOperationException>(() => context.Enter(Guid.NewGuid()));
    }

    private sealed class StubUser(Guid? tenantId) : ICurrentUser
    {
        public Guid? TenantId => tenantId;

        public Guid? UserId => null;

        public TenantRole? Role => null;
    }
}
