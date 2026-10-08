using System.Security.Cryptography;
using System.Text;

namespace AISupportOps.Application.Identity;

/// <summary>Builds URL-friendly, collision-resistant organization slugs, e.g. "acme-corp-3f9a1c".</summary>
public static class Slug
{
    private const int MaxBaseLength = 40;

    public static string Create(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name.Trim())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var baseSlug = builder.ToString().Trim('-');
        if (baseSlug.Length > MaxBaseLength)
        {
            baseSlug = baseSlug[..MaxBaseLength].TrimEnd('-');
        }

        var suffix = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3));
        return baseSlug.Length == 0 ? $"org-{suffix}" : $"{baseSlug}-{suffix}";
    }
}
