using Microsoft.AspNetCore.Http;

namespace Auth.Security;

/// <summary>
/// The single source of truth for the auth cookie attributes (step 4.2
/// correction): every Set-Cookie in the service (login, register, refresh,
/// logout) must go through these options so the browser flags cannot drift
/// between the call sites.
///
///   jwt — JWT access cookie:  HttpOnly, Secure, SameSite=Lax,    Path=/
///   rt  — refresh-token cookie: HttpOnly, Secure, SameSite=Strict, Path=/api/auth
///
/// The jwt flags match the monolith's legacy AuthCookie; rt is Strict and
/// path-restricted because a refresh token is a standing credential that must
/// never leave the auth endpoints.
/// </summary>
public static class AuthCookies
{
    public const string JwtName = "jwt";

    public const string RefreshName = "rt";

    /// <summary>The JWT access-token cookie options. MaxAge = the token lifetime.</summary>
    public static CookieOptions Jwt(TimeSpan maxAge) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        MaxAge = maxAge,
    };

    /// <summary>The refresh-token cookie options. MaxAge = the token expiry.</summary>
    public static CookieOptions Refresh(string path, TimeSpan maxAge) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = path,
        MaxAge = maxAge,
    };

    /// <summary>Deletion cookie for the jwt cookie (Max-Age=0, same flags).</summary>
    public static CookieOptions DeletedJwt() => Jwt(TimeSpan.Zero);

    /// <summary>Deletion cookie for the rt cookie (Max-Age=0, same flags).</summary>
    public static CookieOptions DeletedRefresh(string path) => Refresh(path, TimeSpan.Zero);
}
