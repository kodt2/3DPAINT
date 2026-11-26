namespace Auth.Options;

/// <summary>
/// RS256 access-token configuration (ADR 0007 / ADR 0018).
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary><c>iss</c> claim.</summary>
    public string Issuer { get; set; } = "3dpaint.auth";

    /// <summary><c>aud</c> claim.</summary>
    public string Audience { get; set; } = "3dpaint";

    /// <summary>Access-token lifetime in minutes (15 — ADR 0018).</summary>
    public int ExpiryMinutes { get; set; } = 15;

    /// <summary>
    /// Path to the current RSA private key (PKCS#8 PEM). Required.
    /// Development: generated on first start and persisted here (named volume).
    /// Production: operator-provided secret file; a missing key is a hard
    /// startup failure (fail-fast).
    /// </summary>
    public string? CurrentKeyFile { get; set; }

    /// <summary>
    /// Optional path to the previous RSA private key (PKCS#8 PEM). When set and
    /// present, tokens signed with it (issued before rotation) still validate.
    /// </summary>
    public string? PreviousKeyFile { get; set; }

    /// <summary>Key size used when generating a key in Development.</summary>
    public int RsaKeySize { get; set; } = 2048;
}
