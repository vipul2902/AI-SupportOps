using System.Security.Cryptography;
using System.Text;

namespace AISupportOps.Application.Identity;

/// <summary>
/// Opaque random tokens (refresh tokens, invitation tokens). Only the SHA-256 hash is stored:
/// the token has 256 bits of entropy, so a fast hash is sufficient (unlike passwords).
/// </summary>
public static class SecureToken
{
    public static string Create() =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
