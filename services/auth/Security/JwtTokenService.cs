using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Auth.Options;
using Microsoft.Extensions.Options;

namespace Auth.Security;

/// <summary>
/// Issues RS256 access tokens (ADR 0007 / ADR 0018): 15-minute lifetime,
/// <c>kid</c> = JWK thumbprint in the header, <c>sub</c> = user id,
/// <c>unique_name</c> = login. The token is assembled explicitly (header,
/// payload, RS256 signature) so the <c>kid</c> is always present.
/// </summary>
public sealed class JwtTokenService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly JwtOptions _options;
    private readonly RsaKeyStore _keys;

    public JwtTokenService(IOptions<JwtOptions> options, RsaKeyStore keys)
    {
        _options = options.Value;
        _keys = keys;
    }

    public string CreateAccessToken(int userId, string login)
    {
        var current = _keys.Current
            ?? throw new InvalidOperationException("The current RSA signing key is not loaded.");

        var now = DateTime.UtcNow;
        var expires = now.AddMinutes(_options.ExpiryMinutes);

        var headerJson = JsonSerializer.Serialize(
            new { alg = "RS256", typ = "JWT", kid = current.Kid },
            JsonOptions);
        var payloadJson = JsonSerializer.Serialize(
            new
            {
                sub = userId.ToString(),
                unique_name = login,
                iss = _options.Issuer,
                aud = _options.Audience,
                nbf = new DateTimeOffset(now).ToUnixTimeSeconds(),
                exp = new DateTimeOffset(expires).ToUnixTimeSeconds(),
            },
            JsonOptions);

        var headerB64 = Base64Url(Encoding.UTF8.GetBytes(headerJson));
        var payloadB64 = Base64Url(Encoding.UTF8.GetBytes(payloadJson));
        var signingInput = $"{headerB64}.{payloadB64}";

        // The key is owned by the RsaKeyStore singleton — never dispose it here;
        // it must stay valid for the JWKS endpoint and for every future token.
        var signature = current.Rsa.SignData(
            Encoding.UTF8.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return $"{signingInput}.{Base64Url(signature)}";
    }

    private static string Base64Url(ReadOnlySpan<byte> value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
