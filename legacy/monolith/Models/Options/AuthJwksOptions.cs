namespace LoginAndReg.Models.Options;

/// <summary>
/// JWKS source of the Auth service (step 4.3a, ADR 0018): the monolith
/// validates Auth-issued RS256 tokens against these keys. The URL is an
/// internal app-network address (no Gateway route to JWKS — the monolith
/// talks to Auth directly). <see cref="RequireHttps"/> stays false for that
/// internal address; it is a config decision, never a hardcode.
/// </summary>
public class AuthJwksOptions
{
    public const string SectionName = "AuthJwks";

    /// <summary>JWKS document of the Auth service (internal network).</summary>
    public string Url { get; init; } = "http://auth:8080/.well-known/jwks.json";

    /// <summary>
    /// When true, a non-https <see cref="Url"/> fails startup. False by
    /// default: the dev/prod stacks reach Auth over the internal http
    /// network (the token itself still travels Secure over the edge).
    /// </summary>
    public bool RequireHttps { get; init; }

    /// <summary>
    /// Minimum interval between JWKS fetches (unknown-<c>kid</c> refresh
    /// throttle, ADR 0018). The first RS256 token triggers the first fetch.
    /// </summary>
    public int RefreshThrottleSeconds { get; init; } = 30;

    /// <summary>Expected <c>iss</c> of Auth tokens (mirrors AUTH_JWT_ISSUER).</summary>
    public string Issuer { get; init; } = "3dpaint.auth";

    /// <summary>Expected <c>aud</c> of Auth tokens (mirrors AUTH_JWT_AUDIENCE).</summary>
    public string Audience { get; init; } = "3dpaint";
}
