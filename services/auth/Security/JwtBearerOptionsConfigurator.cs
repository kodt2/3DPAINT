using Auth.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Auth.Security;

/// <summary>
/// Wires the RS256 signing keys (current + previous) into the JwtBearer
/// <see cref="TokenValidationParameters"/>. Invoked from the AddJwtBearer
/// options callback in Program.cs, after the <see cref="RsaKeyStore"/> is
/// loaded at startup (fail-fast, ADR 0018).
/// </summary>
public static class JwtBearerOptionsConfigurator
{
    public static void WireSigningKeys(RsaKeyStore keys, JwtOptions options, JwtBearerOptions jwtOptions)
    {
        // Current + previous key: tokens signed just before a rotation keep
        // validating until they expire (ADR 0007 / ADR 0018).
        var signingKeys = new List<SecurityKey>();
        if (keys.Current is not null)
        {
            signingKeys.Add(new RsaSecurityKey(keys.Current.Rsa));
        }

        if (keys.Previous is not null)
        {
            signingKeys.Add(new RsaSecurityKey(keys.Previous.Rsa));
        }

        jwtOptions.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = options.Issuer,
            ValidateAudience = true,
            ValidAudience = options.Audience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            IssuerSigningKeys = signingKeys,
        };
    }
}
