using System.Globalization;
using AISupportOps.Infrastructure.Caching;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AISupportOps.Api.Auth;

public static class RateLimitPolicies
{
    /// <summary>Per client IP: slows credential stuffing and brute force on auth endpoints.</summary>
    public const string Auth = "auth";

    /// <summary>Per user: LLM calls cost money, so each account gets a budget.</summary>
    public const string Ai = "ai";
}

/// <summary>
/// Endpoint filter applying a Redis-backed limit, so the limit holds across all API instances.
/// Responds 429 with Retry-After (RFC 9110) and a ProblemDetails body.
/// </summary>
internal sealed class RateLimitFilter(string policy) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var configuration = http.RequestServices.GetRequiredService<IConfiguration>();
        var (partition, limit) = policy switch
        {
            RateLimitPolicies.Auth => (
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                configuration.GetValue("RateLimiting:AuthPermitsPerMinute", 10)),
            RateLimitPolicies.Ai => (
                http.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                configuration.GetValue("RateLimiting:AiRequestsPerMinute", 30)),
            _ => throw new InvalidOperationException($"Unknown rate limit policy '{policy}'."),
        };

        var limiter = http.RequestServices.GetRequiredService<IRateLimiter>();
        var decision = await limiter.AcquireAsync($"{policy}:{partition}", limit, TimeSpan.FromMinutes(1));
        http.Response.Headers["X-RateLimit-Limit"] = limit.ToString(CultureInfo.InvariantCulture);
        http.Response.Headers["X-RateLimit-Remaining"] = decision.Remaining.ToString(CultureInfo.InvariantCulture);

        if (decision.Allowed)
        {
            return await next(context);
        }

        var retryAfter = Math.Max(1, (int)Math.Ceiling(decision.RetryAfter.TotalSeconds));
        http.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        return TypedResults.Problem(new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Too many requests",
            Detail = $"Rate limit exceeded. Retry after {retryAfter} seconds.",
        });
    }
}

internal static class RateLimitFilterExtensions
{
    public static TBuilder RequireRedisRateLimit<TBuilder>(this TBuilder builder, string policy)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(new RateLimitFilter(policy));
}
