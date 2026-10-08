using AISupportOps.Application.Identity;

namespace AISupportOps.UnitTests.Identity;

public class TokenAndSlugTests
{
    [Fact]
    public void SecureToken_is_random_url_safe_and_hash_is_deterministic()
    {
        var a = SecureToken.Create();
        var b = SecureToken.Create();

        Assert.NotEqual(a, b);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", a);
        Assert.Equal(SecureToken.Hash(a), SecureToken.Hash(a));
        Assert.NotEqual(SecureToken.Hash(a), SecureToken.Hash(b));
        Assert.Equal(64, SecureToken.Hash(a).Length);
    }

    [Theory]
    [InlineData("Acme Corp", "^acme-corp-[0-9a-f]{6}$")]
    [InlineData("  Ünïcode & Co.!! ", "^n-code-co-[0-9a-f]{6}$")]
    [InlineData("!!!", "^org-[0-9a-f]{6}$")]
    public void Slug_is_lowercase_ascii_with_random_suffix(string name, string pattern) =>
        Assert.Matches(pattern, Slug.Create(name));
}
