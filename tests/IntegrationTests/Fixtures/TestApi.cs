using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AISupportOps.Application.Identity;
using AISupportOps.Application.Tenants;
using AISupportOps.Domain.Tenants;

namespace AISupportOps.IntegrationTests.Fixtures;

/// <summary>Small helpers so tests read as scenarios rather than HTTP plumbing.</summary>
public static class TestApi
{
    public const string Password = "correct-horse-battery-staple";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string UniqueEmail(string prefix = "user") => $"{prefix}-{Guid.NewGuid():N}@example.test";

    public static async Task<AuthResponse> RegisterAsync(this HttpClient client, string? email = null, string org = "Acme")
    {
        var response = await client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest(email ?? UniqueEmail(), Password, "Test User", org), Json);
        return await response.ReadAsync<AuthResponse>(HttpStatusCode.OK);
    }

    public static HttpClient CreateClient(this ApiFactory factory, AuthResponse auth)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    /// <summary>Owner invites a new person with <paramref name="role"/>; returns the new member's session.</summary>
    public static async Task<AuthResponse> AddMemberAsync(this ApiFactory factory, AuthResponse owner, TenantRole role)
    {
        using var ownerClient = factory.CreateClient(owner);
        var email = UniqueEmail(role.ToString().ToUpperInvariant());
        var invite = await (await ownerClient.PostAsJsonAsync("/api/team/invitations", new CreateInvitationRequest(email, role), Json))
            .ReadAsync<InvitationCreatedResponse>(HttpStatusCode.Created);

        using var anonymous = factory.CreateClient();
        var accepted = await anonymous.PostAsJsonAsync("/api/auth/accept-invitation",
            new AcceptInvitationRequest(invite.Token, Password, $"New {role}"), Json);
        return await accepted.ReadAsync<AuthResponse>(HttpStatusCode.OK);
    }

    public static async Task<Guid> UserIdOfAsync(this ApiFactory factory, AuthResponse auth)
    {
        using var client = factory.CreateClient(auth);
        var me = await (await client.GetAsync(new Uri("/api/me", UriKind.Relative))).ReadAsync<MeResponse>(HttpStatusCode.OK);
        return me.UserId;
    }

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {expected} but got {response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, Json)!;
    }
}
