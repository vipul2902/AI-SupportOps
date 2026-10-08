using System.ComponentModel.DataAnnotations;

namespace AISupportOps.Application.Identity;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    [Required]
    public string Issuer { get; set; } = "ai-supportops";

    [Required]
    public string Audience { get; set; } = "ai-supportops-api";

    /// <summary>HMAC-SHA256 key. Must come from secrets, never appsettings. At least 32 bytes.</summary>
    [Required]
    [MinLength(32)]
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>Short-lived so role changes and removals take effect quickly.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "01:00:00")]
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    [Range(typeof(TimeSpan), "01:00:00", "90.00:00:00")]
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(14);

    [Range(typeof(TimeSpan), "01:00:00", "30.00:00:00")]
    public TimeSpan InvitationLifetime { get; set; } = TimeSpan.FromDays(7);
}
