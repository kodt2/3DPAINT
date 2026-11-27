using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace LoginAndReg.Services;

/// <summary>
/// Issues the legacy HS256 access tokens (monolith-issued, ADR 0011).
/// Step 4.3a: the tokens carry <c>iss</c>/<c>aud</c> claims (the values were
/// always in the config; now they are written AND validated on read), and
/// the monolith also accepts RS256 tokens from the Auth service (JWKS —
/// see <c>JwksKeyProvider</c> and the two JwtBearer schemes in Program.cs).
/// </summary>
public class JwtService
{
    private readonly string _secret;
    private readonly int _expiryMinutes;
    private readonly string _issuer;
    private readonly string _audience;

    public JwtService(string secret, int expiryMinutes, string issuer, string audience)
    {
        _secret = secret;
        _expiryMinutes = expiryMinutes;
        _issuer = issuer;
        _audience = audience;
    }

    public string GenerateToken(string username)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.UTF8.GetBytes(_secret);

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, username)
            }),
            Issuer = _issuer,
            Audience = _audience,
            Expires = DateTime.UtcNow.AddMinutes(_expiryMinutes),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(key),
                SecurityAlgorithms.HmacSha256Signature)
        };

        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }
}
