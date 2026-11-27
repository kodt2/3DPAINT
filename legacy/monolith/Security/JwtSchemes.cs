using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace LoginAndReg.Security;

/// <summary>
/// The two JWT schemes the monolith accepts (step 4.3a):
///
/// - the default "Bearer" scheme — the legacy HS256 tokens issued by the
///   monolith itself (secret from Jwt:Key);
/// - <see cref="Rs256"/> — the RS256 tokens issued by the Auth service,
///   validated against its JWKS (JwksKeyProvider).
///
/// The scheme is chosen per request from the JOSE <c>alg</c> header, so a
/// token can never be validated by the wrong scheme (an RS256 token fails
/// the HS256 signature and vice versa — the selection only decides which
/// 401 reason the client sees).
/// </summary>
public static class JwtSchemes
{
    public const string Rs256 = "JwtRs256";

    /// <summary>
    /// Picks the scheme for the raw cookie token. Unrecognized or malformed
    /// tokens fall back to the legacy scheme: they fail its validation with
    /// the same 401 as before.
    /// </summary>
    public static string ForToken(string? token)
        => JwtTokenHeader.TryParse(token, out var alg, out _)
           && string.Equals(alg, "RS256", StringComparison.Ordinal)
            ? Rs256
            : JwtBearerDefaults.AuthenticationScheme;
}
