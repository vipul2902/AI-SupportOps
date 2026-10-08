using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using AISupportOps.Application.Identity;
using AISupportOps.Domain.Tenants;
using AISupportOps.IntegrationTests.Fixtures;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using static AISupportOps.IntegrationTests.Fixtures.TestApi;

namespace AISupportOps.IntegrationTests;

[Collection(ApiCollection.Name)]
public class AuthTests(ApiFactory factory)
{
    [Fact]
    public async Task Register_creates_organization_with_caller_as_owner()
    {
        using var client = factory.CreateClient();
        var email = UniqueEmail();

        var auth = await client.RegisterAsync(email, "Globex");

        Assert.Equal(TenantRole.Owner, auth.Role);
        using var authed = factory.CreateClient(auth);
        var me = await (await authed.GetAsync(new Uri("/api/me", UriKind.Relative))).ReadAsync<MeResponse>(HttpStatusCode.OK);
        Assert.Equal(email, me.Email);
        Assert.Equal(auth.TenantId, me.TenantId);
        Assert.Single(me.Memberships);
        Assert.Equal("Globex", me.Memberships[0].TenantName);
    }

    [Fact]
    public async Task Register_with_existing_email_returns_409_case_insensitively()
    {
        using var client = factory.CreateClient();
        var email = UniqueEmail();
        await client.RegisterAsync(email);

        var response = await client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest(email.ToUpperInvariant(), Password, "Dup", "Dup Org"), Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData("not-an-email", "correct-horse-battery-staple")]
    [InlineData("valid@example.test", "short")]
    public async Task Register_with_invalid_input_returns_400_validation_problem(string email, string password)
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest(email, password, "Name", "Org"), Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("errors", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_succeeds_with_correct_password_and_fails_uniformly_otherwise()
    {
        using var client = factory.CreateClient();
        var email = UniqueEmail();
        await client.RegisterAsync(email);

        var ok = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password, null), Json);
        var wrongPassword = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "wrong-password-123", null), Json);
        var unknownUser = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(UniqueEmail(), Password, null), Json);

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);
        // Same message for both failures: the API must not reveal which emails exist.
        Assert.Equal(await wrongPassword.Content.ReadAsStringAsync().ContinueWith(t => Detail(t.Result)),
                     await unknownUser.Content.ReadAsStringAsync().ContinueWith(t => Detail(t.Result)));
    }

    [Fact]
    public async Task Protected_endpoint_requires_a_valid_token()
    {
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(new Uri("/api/me", UriKind.Relative))).StatusCode);

        using var forged = factory.CreateClient();
        forged.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ForgeToken("attacker-key-attacker-key-attacker-key!!"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await forged.GetAsync(new Uri("/api/me", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Refresh_rotates_token_and_reuse_of_old_token_revokes_the_session()
    {
        using var client = factory.CreateClient();
        var first = await client.RegisterAsync();

        var second = await (await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(first.RefreshToken), Json))
            .ReadAsync<AuthResponse>(HttpStatusCode.OK);
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);

        // Replaying the rotated token looks like theft -> rejected, and the whole family is revoked.
        var replay = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(first.RefreshToken), Json);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        var legit = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(second.RefreshToken), Json);
        Assert.Equal(HttpStatusCode.Unauthorized, legit.StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_refresh_token()
    {
        using var client = factory.CreateClient();
        var auth = await client.RegisterAsync();

        var logout = await client.PostAsJsonAsync("/api/auth/logout", new RefreshRequest(auth.RefreshToken), Json);
        var refresh = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(auth.RefreshToken), Json);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    private static string Detail(string problemJson) =>
        System.Text.Json.JsonDocument.Parse(problemJson).RootElement.GetProperty("detail").GetString()!;

    private static string ForgeToken(string key) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "ai-supportops",
            Audience = "ai-supportops-api",
            Expires = DateTime.UtcNow.AddMinutes(5),
            Subject = new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString()), new Claim("tid", Guid.NewGuid().ToString()), new Claim("role", "Owner")]),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256),
        });
}
