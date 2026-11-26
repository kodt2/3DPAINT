namespace Auth.Security;

/// <summary>
/// Builds the JSON Web Key Set (RFC 7517) served at
/// <c>/.well-known/jwks.json</c> (ADR 0007 / ADR 0018). Includes the current
/// key and, when present, the previous key — so downstream consumers can keep
/// validating tokens issued just before a rotation.
/// </summary>
public sealed class JwksService
{
    private readonly RsaKeyStore _keys;

    public JwksService(RsaKeyStore keys)
    {
        _keys = keys;
    }

    /// <summary>The JWKS document body.</summary>
    public object BuildJwks()
    {
        var entries = new List<object>();
        if (_keys.Current is not null)
        {
            entries.Add(BuildJwk(_keys.Current));
        }

        if (_keys.Previous is not null)
        {
            entries.Add(BuildJwk(_keys.Previous));
        }

        return new { keys = entries };
    }

    private static object BuildJwk(SigningKey key)
    {
        var p = key.Rsa.ExportParameters(false);
        return new
        {
            kty = "RSA",
            use = "sig",
            alg = "RS256",
            kid = key.Kid,
            n = RsaKeyStore.ToBase64Url(p.Modulus!),
            e = RsaKeyStore.ToBase64Url(p.Exponent!),
        };
    }
}
