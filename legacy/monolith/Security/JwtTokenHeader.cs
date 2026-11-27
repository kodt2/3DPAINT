using System.Text;
using System.Text.Json;

namespace LoginAndReg.Security;

/// <summary>
/// Parses the JOSE header of a JWT without validating it (step 4.3a): the
/// monolith needs the <c>alg</c>/<c>kid</c> to pick the JwtBearer scheme
/// (HS256 legacy vs RS256-from-Auth) and to prefetch the signing key from
/// the Auth JWKS before validation. Untrusted input: any malformed token is
/// simply "unrecognized" and falls back to the legacy scheme, where it fails
/// signature validation with a 401.
/// </summary>
public static class JwtTokenHeader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reads <c>alg</c> and <c>kid</c> from the header segment. Returns
    /// false (both out-params null) for anything that is not a
    /// <c>header.payload.signature</c> token with a JSON header.
    /// </summary>
    public static bool TryParse(string? token, out string? alg, out string? kid)
    {
        alg = null;
        kid = null;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var parts = token.Split('.');
        if (parts.Length < 3)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(Base64UrlDecode(parts[0]));
            var root = doc.RootElement;
            alg = root.TryGetProperty("alg", out var algEl) && algEl.ValueKind == JsonValueKind.String
                ? algEl.GetString()
                : null;
            kid = root.TryGetProperty("kid", out var kidEl) && kidEl.ValueKind == JsonValueKind.String
                ? kidEl.GetString()
                : null;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static string Base64UrlDecode(string segment)
    {
        var padded = segment.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
            case 1: return string.Empty;
        }
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }
}
