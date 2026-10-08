using AISupportOps.Domain.Tenants;
using static AISupportOps.Domain.Tenants.TenantRole;

namespace AISupportOps.UnitTests.Domain;

public class RolePolicyTests
{
    [Theory]
    [InlineData(Owner, true)]
    [InlineData(Admin, true)]
    [InlineData(Agent, false)]
    [InlineData(Viewer, false)]
    public void Only_admins_and_owners_manage_members(TenantRole actor, bool expected) =>
        Assert.Equal(expected, RolePolicy.CanManageMembers(actor));

    [Theory]
    [InlineData(Owner, Viewer, Owner, true)]   // owner can promote anyone to owner
    [InlineData(Owner, Owner, Admin, true)]    // owner can demote another owner
    [InlineData(Admin, Viewer, Admin, true)]   // admin can promote to admin
    [InlineData(Admin, Agent, Owner, false)]   // admin cannot create owners
    [InlineData(Admin, Owner, Viewer, false)]  // admin cannot touch owners
    [InlineData(Agent, Viewer, Agent, false)]  // agents cannot manage roles
    [InlineData(Viewer, Viewer, Agent, false)]
    public void Role_change_rules(TenantRole actor, TenantRole current, TenantRole requested, bool expected) =>
        Assert.Equal(expected, RolePolicy.CanChangeRole(actor, current, requested));

    [Theory]
    [InlineData(Owner, Owner, true)]
    [InlineData(Admin, Admin, true)]
    [InlineData(Admin, Owner, false)]
    [InlineData(Agent, Viewer, false)]
    public void Remove_rules(TenantRole actor, TenantRole target, bool expected) =>
        Assert.Equal(expected, RolePolicy.CanRemove(actor, target));
}
