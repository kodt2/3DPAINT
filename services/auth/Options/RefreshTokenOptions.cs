namespace Auth.Options;

/// <summary>
/// Refresh-token configuration (ADR 0018). Step 4.1 issues the token and the
/// http-only <c>rt</c> cookie at login/register; the <c>/refresh</c> endpoint,
/// rotation and lockout land in step 4.2.
/// </summary>
public sealed class RefreshTokenOptions
{
    public const string SectionName = "RefreshToken";

    /// <summary>Cookie name for the opaque refresh token.</summary>
    public const string CookieName = "rt";

    /// <summary>Refresh-token lifetime in days.</summary>
    public int ExpiryDays { get; set; } = 30;

    /// <summary>
    /// Path the <c>rt</c> cookie is sent to. Restricted to the auth endpoints so
    /// the browser never sends the refresh token to unrelated routes.
    /// </summary>
    public string CookiePath { get; set; } = "/api/auth";
}
