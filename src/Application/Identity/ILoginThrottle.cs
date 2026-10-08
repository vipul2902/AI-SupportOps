using AISupportOps.Application.Common;

namespace AISupportOps.Application.Identity;

/// <summary>
/// Per-account brute-force protection, complementing per-IP rate limiting: password spraying from many
/// IPs against one account is still stopped. Keyed by the normalized email whether or not the account
/// exists, so lockout behaviour cannot be used to discover which emails are registered.
/// Trade-off: an attacker can deliberately lock a victim out; the lock is short and time-boxed for that reason.
/// </summary>
public interface ILoginThrottle
{
    /// <summary>
    /// Atomically reserves one sign-in attempt for the account and returns false if the account has used
    /// up its attempts in the current window. Reserving BEFORE the (slow) password check means parallel
    /// requests cannot all slip past a check-then-increment race.
    /// </summary>
    Task<bool> TryReserveAttemptAsync(string normalizedEmail, CancellationToken ct);

    /// <summary>Clears the counter after a successful sign-in.</summary>
    Task ResetAsync(string normalizedEmail, CancellationToken ct);
}

public sealed class AccountLockedException()
    : AppException("Too many failed sign-in attempts for this account. Try again in 15 minutes.");
