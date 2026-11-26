using System.Security.Cryptography;
using System.Text;
using Auth.Data;
using Auth.Entities;
using Auth.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Auth.Services;

/// <summary>
/// Issues opaque refresh tokens and stores only their SHA-256 hash (ADR 0018).
/// Step 4.1 issues the token + http-only cookie at login/register; rotation,
/// reuse detection and lockout are step 4.2.
/// </summary>
public sealed class RefreshTokenService
{
    private readonly AuthDbContext _db;
    private readonly RefreshTokenOptions _options;

    public RefreshTokenService(AuthDbContext db, IOptions<RefreshTokenOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    /// <summary>
    /// Creates a new refresh token for the user (a new rotation family) and
    /// returns the raw token. The raw value is returned once and never stored;
    /// only its hash is persisted.
    /// </summary>
    public async Task<string> CreateForUserAsync(int userId, CancellationToken ct)
    {
        var raw = GenerateRawToken();
        var now = DateTime.UtcNow;

        _db.RefreshTokens.Add(new RefreshToken
        {
            TokenHash = Hash(raw),
            UserId = userId,
            FamilyId = Guid.NewGuid(),
            CreatedAt = now,
            ExpiresAt = now.AddDays(_options.ExpiryDays),
        });
        await _db.SaveChangesAsync(ct);

        return raw;
    }

    /// <summary>SHA-256 (lower-case hex) — the only form of the token stored in the DB.</summary>
    public static string Hash(string raw)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();

    /// <summary>256 bits of CSPRNG entropy, base64url (no padding).</summary>
    private static string GenerateRawToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
