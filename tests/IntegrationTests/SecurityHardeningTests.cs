using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AISupportOps.Api.Infrastructure;
using AISupportOps.Application.Ai;
using AISupportOps.Application.Chat;
using AISupportOps.Application.Identity;
using AISupportOps.Domain.Chat;
using AISupportOps.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static AISupportOps.IntegrationTests.Fixtures.TestApi;

namespace AISupportOps.IntegrationTests;

[Collection(ApiCollection.Name)]
public class SecurityHardeningTests(ApiFactory factory)
{
    // ---------------- refresh cookie ----------------

    [Fact]
    public async Task Cookie_mode_keeps_the_refresh_token_out_of_javascript_and_rotates_via_the_cookie()
    {
        using var browser = Browser();
        var email = UniqueEmail();
        await RegisterViaBodyAsync(email);

        var login = await PostAuthAsync(browser, "/api/auth/login", new LoginRequest(email, Password, null));
        var body = await login.ReadAsync<AuthResponse>(HttpStatusCode.OK);
        var setCookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));

        Assert.Equal(string.Empty, body.RefreshToken); // never exposed to script
        Assert.NotEmpty(body.AccessToken);
        Assert.StartsWith("aiso_refresh=", setCookie, StringComparison.Ordinal);
        foreach (var attribute in new[] { "httponly", "secure", "samesite=strict", "path=/api/auth" })
        {
            Assert.Contains(attribute, setCookie, StringComparison.OrdinalIgnoreCase);
        }

        // The browser sends the cookie automatically; no token in the request body.
        var refreshed = await (await PostAuthAsync(browser, "/api/auth/refresh", null)).ReadAsync<AuthResponse>(HttpStatusCode.OK);
        Assert.NotEqual(body.AccessToken, refreshed.AccessToken);

        // Logout revokes the session and clears the cookie; the next refresh fails.
        Assert.Equal(HttpStatusCode.NoContent, (await PostAuthAsync(browser, "/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAuthAsync(browser, "/api/auth/refresh", null)).StatusCode);
    }

    [Fact]
    public async Task Cookie_is_ignored_without_the_opt_in_header_which_blocks_csrf()
    {
        using var browser = Browser();
        var email = UniqueEmail();
        await RegisterViaBodyAsync(email);
        await PostAuthAsync(browser, "/api/auth/login", new LoginRequest(email, Password, null));

        // What a cross-site form post could do: the cookie may be attached, but no custom header.
        var forged = await browser.PostAsync(new Uri("/api/auth/refresh", UriKind.Relative), JsonContent.Create(new { }));

        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
    }

    // ---------------- account lockout ----------------

    [Fact]
    public async Task Repeated_failures_lock_the_account_even_for_the_correct_password()
    {
        using var client = factory.CreateClient();
        var email = UniqueEmail();
        await client.RegisterAsync(email);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, email, "wrong-password-123")).StatusCode);
        }

        var locked = await LoginAsync(client, email, Password);
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Contains("Try again in 15 minutes", await locked.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lockout_behaves_identically_for_unknown_emails_so_it_cannot_enumerate_accounts()
    {
        using var client = factory.CreateClient();
        var known = UniqueEmail();
        var unknown = UniqueEmail();
        await client.RegisterAsync(known);

        for (var i = 0; i < 5; i++)
        {
            await LoginAsync(client, known, "wrong-password-123");
            await LoginAsync(client, unknown, "wrong-password-123");
        }

        var knownLocked = await LoginAsync(client, known, "wrong-password-123");
        var unknownLocked = await LoginAsync(client, unknown, "wrong-password-123");
        Assert.Equal((HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests), (knownLocked.StatusCode, unknownLocked.StatusCode));
        Assert.Equal(Detail(await knownLocked.Content.ReadAsStringAsync()), Detail(await unknownLocked.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Parallel_guesses_cannot_exceed_the_per_account_limit()
    {
        using var client = factory.CreateClient();
        var email = UniqueEmail();
        await client.RegisterAsync(email);

        // Regression for a check-then-increment race: all requests in flight at once.
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => LoginAsync(client, email, "wrong-password-123")));

        Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.Unauthorized));
        Assert.Equal(15, responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
    }

    [Theory]
    [InlineData("Production", null, null, true)]
    [InlineData("Production", "10.0.0.0/8", null, false)]
    [InlineData("Production", null, "true", false)]
    [InlineData("Development", null, null, false)]
    public void Production_refuses_to_start_without_trusted_proxy_configuration(string environment, string? network, string? noProxy, bool throws)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:KnownNetworks:0"] = network,
            ["ForwardedHeaders:NoProxy"] = noProxy,
        }).Build();
        var env = new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = environment };

        var error = Record.Exception(() => SecurityMiddleware.ValidateForwardedHeadersConfiguration(configuration, env));

        Assert.Equal(throws, error is InvalidOperationException);
    }

    [Fact]
    public async Task Successful_login_resets_the_failure_count()
    {
        using var client = factory.CreateClient();
        var email = UniqueEmail();
        await client.RegisterAsync(email);

        for (var round = 0; round < 2; round++)
        {
            for (var i = 0; i < 4; i++)
            {
                await LoginAsync(client, email, "wrong-password-123");
            }

            Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, email, Password)).StatusCode);
        }
    }

    // ---------------- headers ----------------

    [Fact]
    public async Task Responses_carry_defense_in_depth_security_headers()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/me", UriKind.Relative)); // even on 401

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("default-src 'none'; frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.False(response.Headers.Contains("Strict-Transport-Security")); // HSTS only over HTTPS outside Development/Testing over HTTP
    }

    [Theory]
    [InlineData("10.0.0.5", "203.0.113.9")]     // request came through a trusted proxy: use the forwarded client IP
    [InlineData("198.51.100.7", "198.51.100.7")] // untrusted peer sending X-Forwarded-For: spoof ignored
    public async Task Forwarded_client_ip_is_honoured_only_from_trusted_proxies(string peer, string expectedClientIp)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddTrustedForwardedHeaders(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ForwardedHeaders:KnownNetworks:0"] = "10.0.0.0/8" })
                .Build())
            .BuildServiceProvider();
        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance,
            services.GetRequiredService<IOptions<ForwardedHeadersOptions>>());
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.9";

        await middleware.Invoke(context);

        Assert.Equal(expectedClientIp, context.Connection.RemoteIpAddress!.ToString());
    }

    // ---------------- client disconnect mid-stream ----------------

    [Fact]
    public async Task Client_disconnect_mid_stream_persists_the_partial_answer_as_interrupted()
    {
        var slow = new SlowStreamingModel();
        using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IAiChatService>(slow)));
        using var anonymous = app.CreateClient();
        using var client = app.CreateClient(await anonymous.RegisterAsync());
        await UploadAsync(client);

        using var cts = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat") { Content = JsonContent.Create(new ChatRequest("How do I reset my password?"), options: Json) };
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
        string? conversationId = null;
        while (await reader.ReadLineAsync(cts.Token) is { } line)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal) && conversationId is null)
            {
                conversationId = JsonDocument.Parse(line[6..]).RootElement.GetProperty("conversationId").GetString();
            }

            if (line.Contains("\"text\"", StringComparison.Ordinal))
            {
                break; // first token received: now the user closes the tab
            }
        }

        await cts.CancelAsync();
        response.Dispose();

        var deadline = DateTime.UtcNow.AddSeconds(15);
        MessageResponse? assistant = null;
        while (DateTime.UtcNow < deadline && assistant is null)
        {
            var detail = await (await client.GetAsync(new Uri($"/api/conversations/{conversationId}", UriKind.Relative))).ReadAsync<ConversationDetail>(HttpStatusCode.OK);
            assistant = detail.Messages.FirstOrDefault(m => m.Role == MessageRole.Assistant);
            await Task.Delay(100);
        }

        Assert.NotNull(assistant);
        Assert.Equal(MessageStatus.Interrupted, assistant.Status);
        Assert.StartsWith("Partial", assistant.Content, StringComparison.Ordinal);
        Assert.False(slow.Completed, "generation should stop when the client disconnects");
    }

    private sealed class SlowStreamingModel : IAiChatService
    {
        public bool Completed { get; private set; }

        public string ModelId => "slow";

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct) =>
            Task.FromResult(new LlmResponse(request.Messages[^1].Content, ModelId, new LlmUsage(1, 1), "stop"));

        public async IAsyncEnumerable<LlmStreamUpdate> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new LlmStreamUpdate("Partial ");
            for (var i = 0; i < 100; i++)
            {
                await Task.Delay(100, ct);
                yield return new LlmStreamUpdate("more ");
            }

            Completed = true;
        }
    }

    // ---------------- helpers ----------------

    private HttpClient Browser() =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true });

    private static Task<HttpResponseMessage> PostAuthAsync(HttpClient client, string path, object? body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = body is null ? null : JsonContent.Create(body, options: Json) };
        request.Headers.Add("X-Auth-Mode", "cookie");
        return client.SendAsync(request);
    }

    private async Task RegisterViaBodyAsync(string email)
    {
        using var client = factory.CreateClient();
        await client.RegisterAsync(email);
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password, null), Json);

    private static string? Detail(string json) => JsonDocument.Parse(json).RootElement.GetProperty("detail").GetString();

    private static async Task UploadAsync(HttpClient client)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent("# Account security\n\nTo reset your password, open Settings, choose Security, then click Reset password."u8.ToArray()), "file", "security.md");
        var created = await (await client.PostAsync(new Uri("/api/documents", UriKind.Relative), form)).ReadAsync<Application.Documents.DocumentResponse>(HttpStatusCode.Created);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((await (await client.GetAsync(new Uri($"/api/documents/{created.Id}", UriKind.Relative))).ReadAsync<Application.Documents.DocumentResponse>(HttpStatusCode.OK)).Status != Domain.Documents.DocumentStatus.Processed
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
    }
}
