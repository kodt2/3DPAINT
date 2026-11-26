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

    /// <summary>
    /// A replay of an already-replaced token inside this window is a duplicate
    /// (401, family kept); outside the window it revokes the whole family.
    /// Covers double-fired browser requests without weakening theft detection.
    /// </summary>
    public int ReuseGracePeriodSeconds { get; set; } = 60;

    /// <summary>Interval between background cleanups of dead refresh-token rows.</summary>
    public int CleanupIntervalHours { get; set; } = 1;

    /// <summary>Batch size for the cleanup (rows per DELETE, looped until empty).</summary>
    public int CleanupBatchSize { get; set; } = 500;

    /// <summary>The grace window as a duration.</summary>
    public TimeSpan ReuseGracePeriod => TimeSpan.FromSeconds(ReuseGracePeriodSeconds);
}
