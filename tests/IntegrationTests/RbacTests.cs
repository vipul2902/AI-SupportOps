using System.Net;
using System.Net.Http.Json;
using AISupportOps.Application.Identity;
using AISupportOps.Application.Tenants;
using AISupportOps.Domain.Tenants;
using AISupportOps.IntegrationTests.Fixtures;
using static AISupportOps.IntegrationTests.Fixtures.TestApi;

namespace AISupportOps.IntegrationTests;

[Collection(ApiCollection.Name)]
public class RbacTests(ApiFactory factory)
{
    [Fact]
    public async Task Invited_user_joins_with_invited_role_and_sees_team()
    {
        using var anonymous = factory.CreateClient();
        var owner = await anonymous.RegisterAsync();

        var agent = await factory.AddMemberAsync(owner, TenantRole.Agent);

        Assert.Equal(TenantRole.Agent, agent.Role);
        Assert.Equal(owner.TenantId, agent.TenantId);
        using var agentClient = factory.CreateClient(agent);
        var members = await (await agentClient.GetAsync(new Uri("/api/team/members", UriKind.Relative)))
            .ReadAsync<List<MemberResponse>>(HttpStatusCode.OK);
        Assert.Equal(2, members.Count);
    }

    [Fact]
    public async Task Invitation_token_cannot_be_used_twice()
    {
        using var anonymous = factory.CreateClient();
        var owner = await anonymous.RegisterAsync();
        using var ownerClient = factory.CreateClient(owner);
        var invite = await (await ownerClient.PostAsJsonAsync("/api/team/invitations",
            new CreateInvitationRequest(UniqueEmail(), TenantRole.Viewer), Json)).ReadAsync<InvitationCreatedResponse>(HttpStatusCode.Created);

        var first = await anonymous.PostAsJsonAsync("/api/auth/accept-invitation", new AcceptInvitationRequest(invite.Token, Password, "A"), Json);
        var second = await anonymous.PostAsJsonAsync("/api/auth/accept-invitation", new AcceptInvitationRequest(invite.Token, Password, "A"), Json);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    [Theory]
    [InlineData(TenantRole.Viewer)]
    [InlineData(TenantRole.Agent)]
    public async Task Non_admins_cannot_invite_or_rename_organization(TenantRole role)
    {
        using var anonymous = factory.CreateClient();
        var owner = await anonymous.RegisterAsync();
        var member = await factory.AddMemberAsync(owner, role);
        using var client = factory.CreateClient(member);

        var invite = await client.PostAsJsonAsync("/api/team/invitations", new CreateInvitationRequest(UniqueEmail(), TenantRole.Viewer), Json);
        var rename = await client.PatchAsJsonAsync("/api/tenant", new RenameTenantRequest("Hijacked"), Json);

        Assert.Equal(HttpStatusCode.Forbidden, invite.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, rename.StatusCode);
    }

    [Fact]
    public async Task Admin_cannot_create_owners_or_modify_owners()
    {
        using var anonymous = factory.CreateClient();
        var owner = await anonymous.RegisterAsync();
        var admin = await factory.AddMemberAsync(owner, TenantRole.Admin);
        var ownerId = await factory.UserIdOfAsync(owner);
        using var adminClient = factory.CreateClient(admin);

        var inviteOwner = await adminClient.PostAsJsonAsync("/api/team/invitations", new CreateInvitationRequest(UniqueEmail(), TenantRole.Owner), Json);
        var demoteOwner = await adminClient.PatchAsJsonAsync($"/api/team/members/{ownerId}/role", new ChangeRoleRequest(TenantRole.Viewer), Json);
        var removeOwner = await adminClient.DeleteAsync(new Uri($"/api/team/members/{ownerId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, inviteOwner.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, demoteOwner.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, removeOwner.StatusCode);
    }

    [Fact]
    public async Task Last_owner_cannot_demote_themselves()
    {
        using var anonymous = factory.CreateClient();
        var owner = await anonymous.RegisterAsync();
        var ownerId = await factory.UserIdOfAsync(owner);
        using var client = factory.CreateClient(owner);

        var response = await client.PatchAsJsonAsync($"/api/team/members/{ownerId}/role", new ChangeRoleRequest(TenantRole.Admin), Json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Removed_member_loses_access_immediately_via_refresh()
    {
        using var anonymous = factory.CreateClient();
        var owner = await anonymous.RegisterAsync();
        var agent = await factory.AddMemberAsync(owner, TenantRole.Agent);
        var agentId = await factory.UserIdOfAsync(agent);
        using var ownerClient = factory.CreateClient(owner);

        var remove = await ownerClient.DeleteAsync(new Uri($"/api/team/members/{agentId}", UriKind.Relative));
        var refresh = await anonymous.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(agent.RefreshToken), Json);
        using var agentClient = factory.CreateClient(agent);
        var me = await agentClient.GetAsync(new Uri("/api/me", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        // The still-valid access token no longer grants data access: membership is re-checked.
        Assert.Equal(HttpStatusCode.Forbidden, me.StatusCode);
    }
}
