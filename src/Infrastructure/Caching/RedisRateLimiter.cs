using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace AISupportOps.Infrastructure.Caching;

public sealed record RateLimitDecision(bool Allowed, long Remaining, TimeSpan RetryAfter);

public interface IRateLimiter
{
    Task<RateLimitDecision> AcquireAsync(string key, int limit, TimeSpan window);
}

/// <summary>
/// Fixed-window rate limiter shared by all API instances via Redis. One atomic Lua script:
/// increment the window's counter and set its expiry on first use, so concurrent requests on
/// different servers cannot race past the limit. Trade-off: a fixed window allows up to 2x the
/// limit across a window boundary; acceptable for abuse and cost control.
/// Fails open: if Redis is down, requests are allowed (availability over strictness) and logged.
/// </summary>
internal sealed partial class RedisRateLimiter(IConnectionMultiplexer redis, ILogger<RedisRateLimiter> logger) : IRateLimiter
{
    private const string Script = """
        local count = redis.call('INCR', KEYS[1])
        if count == 1 then
          redis.call('PEXPIRE', KEYS[1], ARGV[1])
        end
        return { count, redis.call('PTTL', KEYS[1]) }
        """;

    public async Task<RateLimitDecision> AcquireAsync(string key, int limit, TimeSpan window)
    {
        try
        {
            var result = (RedisResult[])(await redis.GetDatabase().ScriptEvaluateAsync(
                Script, [new RedisKey($"rl:{key}")], [(long)window.TotalMilliseconds]))!;
            var count = (long)result[0];
            var ttl = TimeSpan.FromMilliseconds(Math.Max((long)result[1], 0));

            return count <= limit
                ? new RateLimitDecision(true, limit - count, TimeSpan.Zero)
                : new RateLimitDecision(false, 0, ttl);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or ObjectDisposedException)
        {
            LogFailOpen(logger, ex, key);
            return new RateLimitDecision(true, limit, TimeSpan.Zero);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rate limiter unavailable for {Key}; allowing request")]
    private static partial void LogFailOpen(ILogger logger, Exception exception, string key);
}
