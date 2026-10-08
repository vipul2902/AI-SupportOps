using Microsoft.AspNetCore.HttpOverrides;

namespace AISupportOps.Api.Infrastructure;

internal static class SecurityMiddleware
{
    /// <summary>
    /// Behind a reverse proxy (nginx, Azure Container Apps ingress) the TCP peer is the proxy, so the real
    /// client IP and scheme arrive in X-Forwarded-For / X-Forwarded-Proto. Those headers are trusted ONLY
    /// from configured proxy networks; otherwise any client could spoof its IP and dodge per-IP limits.
    /// Configure with ForwardedHeaders:KnownNetworks (CIDR list). Loopback is always trusted.
    /// </summary>
    public static IServiceCollection AddTrustedForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        var networks = TrustedNetworks(configuration);
        services.Configure<ForwardedHeadersOptions>(options => ApplyTrustedNetworks(options, networks));
        return services;
    }

    /// <summary>
    /// Fail fast on a dangerous misconfiguration: behind a proxy with no trusted networks, every client
    /// appears to share the proxy's IP, so per-IP limits become one global bucket a single attacker can
    /// exhaust. Production must either list the proxy networks or explicitly state there is no proxy.
    /// </summary>
    public static void ValidateForwardedHeadersConfiguration(IConfiguration configuration, IHostEnvironment environment)
    {
        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            return;
        }

        var networks = TrustedNetworks(configuration);
        if (networks.Length == 0 && !configuration.GetValue<bool>("ForwardedHeaders:NoProxy"))
        {
            throw new InvalidOperationException(
                "ForwardedHeaders:KnownNetworks is empty. Set it to the reverse proxy / ingress CIDR(s), or set ForwardedHeaders:NoProxy=true if clients connect directly.");
        }
    }

    // Blank entries (e.g. from an empty environment variable) do not count as configuration.
    private static string[] TrustedNetworks(IConfiguration configuration) =>
        (configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [])
            .Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();

    private static void ApplyTrustedNetworks(ForwardedHeadersOptions options, string[] networks)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1; // exactly one proxy hop in front of the API
        foreach (var cidr in networks)
        {
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
        }
    }

    /// <summary>
    /// Defense-in-depth response headers. The API only serves JSON and file downloads, so its CSP can be
    /// maximally strict; the SPA's own CSP is set by the web server that serves it.
    /// </summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, IWebHostEnvironment environment) =>
        app.Use(async (http, next) =>
        {
            http.Response.OnStarting(() =>
            {
                var headers = http.Response.Headers;
                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY";
                headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
                headers["Referrer-Policy"] = "no-referrer";
                headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
                headers["Cross-Origin-Opener-Policy"] = "same-origin";
                if (http.Request.IsHttps && !environment.IsDevelopment())
                {
                    headers.StrictTransportSecurity = "max-age=31536000; includeSubDomains";
                }

                return Task.CompletedTask;
            });
            await next();
        });
}
