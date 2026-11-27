using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace LoginAndReg.Tests;

/// <summary>
/// Builds test JWTs in the exact shapes the two production issuers produce:
///
/// - HS256: the legacy monolith token — the <c>ClaimTypes.Name</c> claim
///   (login) plus iss/aud (step 4.3a added the latter to JwtService).
/// - RS256: the Auth service token — <c>sub</c> (user id as a string),
///   <c>unique_name</c> (login), iss/aud/nbf/exp and a <c>kid</c> header
///   (services/auth JwtTokenService).
/// </summary>
public static class TestTokens
{
    public static RsaSecurityKey NewRsaKey()
    {
        // RsaSecurityKey takes ownership of the RSA instance: do NOT dispose
        // the RSA here, or every use of the key throws ObjectDisposedException.
        return new RsaSecurityKey(RSA.Create(2048));
    }

    public static string MakeRs256(
        RsaSecurityKey signingKey,
        string kid,
        string sub,
        string login,
        string issuer = MonolithAppFactory.AuthIssuer,
        string audience = MonolithAppFactory.AuthAudience,
        DateTimeOffset? expires = null,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? issuedAt = null)
    {
        var handler = new JwtSecurityTokenHandler();
        var now = DateTimeOffset.UtcNow;
        var exp = expires ?? now.AddMinutes(5);
        // nbf must stay before exp (IDX12401): an explicitly shortened
        // validity window shifts nbf back as well, a plain valid token
        // gets a nbf one minute in the past.
        var nbf = notBefore ?? (expires.HasValue ? exp.AddMinutes(-5) : now.AddMinutes(-1));
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim("sub", sub),
                new Claim("unique_name", login),
            }),
            SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256),
            Issuer = issuer,
            Audience = audience,
            // SITJ 8.x uses UTC DateTime; the public API stays DateTimeOffset.
            IssuedAt = (issuedAt ?? now).UtcDateTime,
            NotBefore = nbf.UtcDateTime,
            Expires = exp.UtcDateTime,
        };

        // SecurityTokenDescriptor has no kid slot in SITJ 8.x: the header
        // field is set on the token explicitly (the Auth service does the
        // same — it assembles the header itself, ADR 0018).
        var token = handler.CreateJwtSecurityToken(descriptor)
            ?? throw new InvalidOperationException("CreateJwtSecurityToken returned null.");
        // JwtHeader is a property bag: the read-only Kid getter reads the
        // "kid" entry, the entry itself is writable.
        token.Header["kid"] = kid;
        return handler.WriteToken(token);
    }

    public static string MakeHs256(
        string secret,
        string login,
        string issuer = MonolithAppFactory.JwtIssuer,
        string audience = MonolithAppFactory.JwtAudience,
        DateTimeOffset? expires = null,
        DateTimeOffset? notBefore = null)
    {
        var handler = new JwtSecurityTokenHandler();
        var now = DateTimeOffset.UtcNow;
        var exp = expires ?? now.AddMinutes(5);
        // nbf must stay before exp (IDX12401) — see MakeRs256.
        var nbf = notBefore ?? (expires.HasValue ? exp.AddMinutes(-5) : now.AddMinutes(-1));
        var descriptor = new SecurityTokenDescriptor
        {
            // The legacy token carries only the login (ClaimTypes.Name),
            // exactly as the monolith's JwtService has always done.
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, login),
            }),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
                SecurityAlgorithms.HmacSha256),
            Issuer = issuer,
            Audience = audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = nbf.UtcDateTime,
            Expires = exp.UtcDateTime,
        };

        return handler.WriteToken(handler.CreateToken(descriptor)!);
    }
}
