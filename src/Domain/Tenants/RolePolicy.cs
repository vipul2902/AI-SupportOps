namespace AISupportOps.Domain.Tenants;

/// <summary>
/// Business rules for who may grant, change, or remove which roles.
/// Owners can do anything; Admins can manage everyone except Owners and cannot create Owners.
/// </summary>
public static class RolePolicy
{
    public static bool CanManageMembers(TenantRole actor) => actor >= TenantRole.Admin;

    public static bool CanAssignRole(TenantRole actor, TenantRole requested) =>
        actor == TenantRole.Owner || (actor == TenantRole.Admin && requested != TenantRole.Owner);

    public static bool CanChangeRole(TenantRole actor, TenantRole current, TenantRole requested) =>
        CanManageMembers(actor)
        && CanModify(actor, current)
        && CanAssignRole(actor, requested);

    public static bool CanRemove(TenantRole actor, TenantRole target) =>
        CanManageMembers(actor) && CanModify(actor, target);

    private static bool CanModify(TenantRole actor, TenantRole target) =>
        actor == TenantRole.Owner || target != TenantRole.Owner;
}
