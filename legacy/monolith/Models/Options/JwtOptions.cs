namespace LoginAndReg.Models.Options;

public class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Key { get; init; } = string.Empty;

    public int ExpiryMinutes { get; init; } = 60;

    /// <summary>
    /// <c>iss</c> claim of the tokens this monolith issues (HS256) and the
    /// one it validates for its own scheme (step 4.3a — the claim was always
    /// in the config but unused; now it is written and validated).
    /// </summary>
    public string Issuer { get; init; } = string.Empty;

    /// <summary><c>aud</c> claim of the tokens this monolith issues (HS256).</summary>
    public string Audience { get; init; } = string.Empty;

    /// <summary>
    /// Clock skew allowed for both token schemes (default 30 s; 0 in the
    /// test stand so expiry tests are exact).
    /// </summary>
    public int ClockSkewSeconds { get; init; } = 30;
}
