using System.Security.Cryptography;
using System.Text;
using AISupportOps.Application.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace AISupportOps.Infrastructure.Caching;

/// <summary>
/// Counts sign-in attempts per account in Redis (15-minute window, shared across instances). One Lua
/// script increments and sets the expiry atomically: no check-then-act race between parallel requests,
/// and no key left without an expiry (which would lock an account forever). The key is a hash of the
/// normalized email, so Redis never stores addresses. Fails open: if Redis is down, sign-in still works
/// and the per-IP limit still applies.
/// </summary>
internal sealed partial class RedisLoginThrottle(IConnectionMultiplexer redis, IConfiguration configuration, ILogger<RedisLoginThrottle> logger) : ILoginThrottle
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private const string ReserveScript = """
        local count = redis.call('INCR', KEYS[1])
        if count == 1 then
          redis.call('PEXPIRE', KEYS[1], ARGV[1])
        end
        return count
        """;

    private int MaxAttempts => configuration.GetValue("Auth:MaxFailedLoginsPerAccount", 5);

    public async Task<bool> TryReserveAttemptAsync(string normalizedEmail, CancellationToken ct)
    {
        try
        {
            var count = (long)await redis.GetDatabase().ScriptEvaluateAsync(
                ReserveScript, [Key(normalizedEmail)], [(long)Window.TotalMilliseconds]);
            // Attempt N is allowed while N <= max; a successful sign-in resets the counter.
            return count <= MaxAttempts;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            LogUnavailable(logger, ex);
            return true;
        }
    }

    public async Task ResetAsync(string normalizedEmail, CancellationToken ct)
    {
        try
        {
            await redis.GetDatabase().KeyDeleteAsync(Key(normalizedEmail));
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            LogUnavailable(logger, ex);
        }
    }

    private static RedisKey Key(string normalizedEmail) =>
        $"login-attempts:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedEmail)))}";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Login throttle unavailable; continuing without per-account lockout")]
    private static partial void LogUnavailable(ILogger logger, Exception exception);
}
