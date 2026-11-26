namespace Auth.Options;

/// <summary>
/// Refresh-token configuration (ADR 0018). The token is issued as the http-only
/// <c>rt</c> cookie at login/register; rotation, reuse detection, lockout and
/// cleanup land in step 4.2. The cookie NAME and flags live in
/// <c>Auth.Security.AuthCookies</c> (single source of truth, step 4.2).
/// </summary>
public sealed class RefreshTokenOptions
{
    public const string SectionName = "RefreshToken";

    /// <summary>Refresh-token lifetime in days.</summary>
    public int ExpiryDays { get; set; } = 30;

    /// <summary>
    /// Path the <c>rt</c> cookie is sent to. Restricted to the auth endpoints so
    /// the browser never sends the refresh token to unrelated routes.
    /// </summary>
    public string CookiePath { get; set; } = "/api/auth";
}
