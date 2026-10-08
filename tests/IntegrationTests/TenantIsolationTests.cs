using System.Net;
using System.Net.Http.Json;
using AISupportOps.Application.Common;
using AISupportOps.Application.Identity;
using AISupportOps.Application.Tenants;
using AISupportOps.Domain.Tenants;
using AISupportOps.Infrastructure.Persistence;
using AISupportOps.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using static AISupportOps.IntegrationTests.Fixtures.TestApi;

namespace AISupportOps.IntegrationTests;

/// <summary>
/// The most important tests in the system: Organization B must never see or modify
/// Organization A's data, through the API or directly through the DbContext.
/// </summary>
[Collection(ApiCollection.Name)]
public class TenantIsolationTests(ApiFactory factory)
{
    [Fact]
    public async Task Members_and_invitations_are_scoped_to_the_callers_tenant()
    {
        var (a, b) = await TwoTenantsAsync();
        using var aClient = factory.CreateClient(a);
        using var bClient = factory.CreateClient(b);
        await aClient.PostAsJsonAsync("/api/team/invitations", new CreateInvitationRequest(UniqueEmail(), TenantRole.Viewer), Json);

        var bMembers = await (await bClient.GetAsync(new Uri("/api/team/members", UriKind.Relative))).ReadAsync<List<MemberResponse>>(HttpStatusCode.OK);
        var bInvites = await (await bClient.GetAsync(new Uri("/api/team/invitations", UriKind.Relative))).ReadAsync<List<InvitationResponse>>(HttpStatusCode.OK);

        Assert.Single(bMembers);
        Assert.Equal(await factory.UserIdOfAsync(b), bMembers[0].UserId);
        Assert.Empty(bInvites);
    }

    [Fact]
    public async Task Cannot_modify_another_tenants_members_or_invitations()
    {
        var (a, b) = await TwoTenantsAsync();
        var aOwnerId = await factory.UserIdOfAsync(a);
        using var aClient = factory.CreateClient(a);
        var aInvite = await (await aClient.PostAsJsonAsync("/api/team/invitations",
            new CreateInvitationRequest(UniqueEmail(), TenantRole.Viewer), Json)).ReadAsync<InvitationCreatedResponse>(HttpStatusCode.Created);
        using var bClient = factory.CreateClient(b);

        var changeRole = await bClient.PatchAsJsonAsync($"/api/team/members/{aOwnerId}/role", new ChangeRoleRequest(TenantRole.Viewer), Json);
        var remove = await bClient.DeleteAsync(new Uri($"/api/team/members/{aOwnerId}", UriKind.Relative));
        var revoke = await bClient.DeleteAsync(new Uri($"/api/team/invitations/{aInvite.Invitation.Id}", UriKind.Relative));

        // 404, not 403: another tenant's resources are indistinguishable from nonexistent ones.
        Assert.Equal(HttpStatusCode.NotFound, changeRole.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, remove.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, revoke.StatusCode);
    }

    [Fact]
    public async Task Cannot_switch_into_or_log_into_a_tenant_without_membership()
    {
        var (a, b) = await TwoTenantsAsync();
        using var bClient = factory.CreateClient(b);
        var bMe = await (await bClient.GetAsync(new Uri("/api/me", UriKind.Relative))).ReadAsync<MeResponse>(HttpStatusCode.OK);

        var switchResponse = await bClient.PostAsJsonAsync("/api/auth/switch-tenant", new SwitchTenantRequest(a.TenantId), Json);
        var login = await bClient.PostAsJsonAsync("/api/auth/login", new LoginRequest(bMe.Email, Password, a.TenantId), Json);

        Assert.Equal(HttpStatusCode.Forbidden, switchResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
    }

    [Fact]
    public async Task User_in_two_tenants_sees_each_tenants_data_only_after_switching()
    {
        var (a, b) = await TwoTenantsAsync();
        using var bClient = factory.CreateClient(b);
        var bMe = await (await bClient.GetAsync(new Uri("/api/me", UriKind.Relative))).ReadAsync<MeResponse>(HttpStatusCode.OK);

        // A invites B's user, who accepts with their existing password.
        using var aClient = factory.CreateClient(a);
        var invite = await (await aClient.PostAsJsonAsync("/api/team/invitations",
            new CreateInvitationRequest(bMe.Email, TenantRole.Agent), Json)).ReadAsync<InvitationCreatedResponse>(HttpStatusCode.Created);
        await (await bClient.PostAsJsonAsync("/api/auth/accept-invitation", new AcceptInvitationRequest(invite.Token, Password, null), Json))
            .ReadAsync<AuthResponse>(HttpStatusCode.OK);

        var inA = await (await bClient.PostAsJsonAsync("/api/auth/switch-tenant", new SwitchTenantRequest(a.TenantId), Json))
            .ReadAsync<AuthResponse>(HttpStatusCode.OK);
        using var inAClient = factory.CreateClient(inA);
        var membersInA = await (await inAClient.GetAsync(new Uri("/api/team/members", UriKind.Relative))).ReadAsync<List<MemberResponse>>(HttpStatusCode.OK);
        var membersInB = await (await bClient.GetAsync(new Uri("/api/team/members", UriKind.Relative))).ReadAsync<List<MemberResponse>>(HttpStatusCode.OK);

        Assert.Equal(TenantRole.Agent, inA.Role);
        Assert.Equal(2, membersInA.Count);
        Assert.Single(membersInB);
    }

    [Fact]
    public async Task DbContext_query_filter_hides_other_tenants_rows_and_fails_closed_without_tenant()
    {
        var (a, b) = await TwoTenantsAsync();

        await using (var asA = CreateDbContext(a.TenantId))
        {
            var tenantIds = await asA.TenantMemberships.Select(m => m.TenantId).Distinct().ToListAsync();
            Assert.Equal([a.TenantId], tenantIds);
        }

        await using (var noTenant = CreateDbContext(null))
        {
            Assert.Equal(0, await noTenant.TenantMemberships.CountAsync());
        }
    }

    [Fact]
    public async Task DbContext_blocks_writes_to_another_tenant()
    {
        var (a, b) = await TwoTenantsAsync();
        await using var asA = CreateDbContext(a.TenantId);
        var bUserId = await factory.UserIdOfAsync(b);

        asA.TenantMemberships.Add(new TenantMembership(b.TenantId, bUserId, TenantRole.Viewer));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => asA.SaveChangesAsync());
        Assert.Contains("Cross-tenant write blocked", ex.Message, StringComparison.Ordinal);
    }

    private async Task<(AuthResponse A, AuthResponse B)> TwoTenantsAsync()
    {
        using var client = factory.CreateClient();
        return (await client.RegisterAsync(org: "Tenant A"), await client.RegisterAsync(org: "Tenant B"));
    }

    private AppDbContext CreateDbContext(Guid? tenantId)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(factory.PostgresConnectionString, o => o.UseVector())
            .UseSnakeCaseNamingConvention()
            .Options;
        return new AppDbContext(options, new FixedTenant(tenantId), TimeProvider.System);
    }

    private sealed class FixedTenant(Guid? tenantId) : ITenantContext
    {
        public Guid? TenantId => tenantId;
    }
}
