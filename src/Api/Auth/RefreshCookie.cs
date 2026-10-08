using AISupportOps.Application.Identity;

namespace AISupportOps.Api.Auth;

/// <summary>
/// Browser session mode. When a request carries <c>X-Auth-Mode: cookie</c>, the refresh token is set as an
/// <c>HttpOnly; Secure; SameSite=Strict</c> cookie scoped to <c>/api/auth</c> and removed from the JSON body,
/// so injected script can never read or exfiltrate it.
///
/// CSRF: the cookie is only honoured when the same custom header is present. A cross-site page cannot add a
/// custom header without a CORS preflight, which this API never approves, and SameSite=Strict keeps the
/// cookie off cross-site requests anyway.
///
/// Non-browser clients omit the header and keep using the token in the body.
/// </summary>
internal static class RefreshCookie
{
    public const string HeaderName = "X-Auth-Mode";
    public const string CookieName = "aiso_refresh";
    private const string CookiePath = "/api/auth";

    public static bool IsCookieMode(HttpContext http) =>
        string.Equals(http.Request.Headers[HeaderName], "cookie", StringComparison.OrdinalIgnoreCase);

    /// <summary>Sets the cookie in cookie mode and strips the token from the body.</summary>
    public static AuthResponse Issue(HttpContext http, AuthResponse response)
    {
        if (!IsCookieMode(http))
        {
            return response;
        }

        http.Response.Cookies.Append(CookieName, response.RefreshToken, new CookieOptions
        {
            HttpOnly = true,
            // Browsers accept Secure cookies on http://localhost; everywhere else the site is HTTPS.
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = CookiePath,
            Expires = response.RefreshTokenExpiresAt,
            IsEssential = true,
        });
        return response with { RefreshToken = string.Empty };
    }

    /// <summary>The refresh token from the body, or from the cookie in cookie mode.</summary>
    public static RefreshRequest Resolve(HttpContext http, RefreshRequest? body)
    {
        if (!string.IsNullOrEmpty(body?.RefreshToken))
        {
            return body;
        }

        var fromCookie = IsCookieMode(http) ? http.Request.Cookies[CookieName] : null;
        return new RefreshRequest(fromCookie ?? string.Empty);
    }

    public static void Clear(HttpContext http)
    {
        if (IsCookieMode(http))
        {
            http.Response.Cookies.Delete(CookieName, new CookieOptions { Path = CookiePath, Secure = true, SameSite = SameSiteMode.Strict, HttpOnly = true });
        }
    }
}
