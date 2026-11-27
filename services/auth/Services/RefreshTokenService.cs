using System.Security.Cryptography;
using System.Text;
using Auth.Data;
using Auth.Entities;
using Auth.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Auth.Services;

/// <summary>The result of a <c>POST /api/auth/refresh</c> rotation attempt.</summary>
public enum RefreshRotationOutcome
{
    /// <summary>The token was valid; it was replaced and a new one issued.</summary>
    Success,

    /// <summary>
    /// The token was already replaced inside the grace period (a duplicate
    /// request, e.g. a double-fired browser). Answer 401, do NOT revoke the
    /// family — the legitimate sibling token must keep working.
    /// </summary>
    Duplicate,

    /// <summary>
    /// The token was already replaced OUTSIDE the grace period: it is being
    /// replayed. The whole rotation family is revoked. Answer 401.
    /// </summary>
    Reused,

    /// <summary>The token is unknown, expired, or already revoked. Answer 401.</summary>
    Invalid,
}

public sealed record RefreshRotationResult(
    RefreshRotationOutcome Outcome,
    string? NewRawToken,
    AuthUser? User);

/// <summary>
/// Issues opaque refresh tokens and stores only their SHA-256 hash (ADR 0018).
/// Step 4.2: rotation with atomic claim, reuse detection (family revocation),
/// logout revocation and batched cleanup of dead rows.
/// </summary>
public sealed class RefreshTokenService
{
    private readonly AuthDbContext _db;
    private readonly RefreshTokenOptions _options;
    private readonly ILogger<RefreshTokenService> _logger;

    public RefreshTokenService(
        AuthDbContext db,
        IOptions<RefreshTokenOptions> options,
        ILogger<RefreshTokenService> logger)
    {
        _db = db;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Creates a new refresh token for the user (a new rotation family) and
    /// returns the raw token. The raw value is returned once and never stored;
    /// only its hash is persisted.
    /// </summary>
    public async Task<string> CreateForUserAsync(Guid userId, CancellationToken ct)
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

    /// <summary>
    /// Rotates a refresh token: the old one is atomically claimed
    /// (<c>UPDATE ... WHERE ReplacedAt IS NULL</c>, affected-rows check) and a
    /// new token of the same family is issued. Concurrency-safe: with N
    /// parallel requests carrying the same token exactly one succeeds.
    /// Reuse of an already-replaced token outside the grace window revokes the
    /// whole family (ADR 0018). The raw token value is never logged.
    /// </summary>
    public async Task<RefreshRotationResult> RotateAsync(string rawOldToken, CancellationToken ct)
    {
        var hash = Hash(rawOldToken);
        var row = await _db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (row is null || row.RevokedAt is not null)
        {
            return new RefreshRotationResult(RefreshRotationOutcome.Invalid, null, null);
        }

        var now = DateTime.UtcNow;
        if (row.ExpiresAt <= now)
        {
            return new RefreshRotationResult(RefreshRotationOutcome.Invalid, null, null);
        }

        if (row.ReplacedAt is not null)
        {
            return await ClassifyReplayAsync(row, now, ct);
        }

        var newRaw = GenerateRawToken();
        var newHash = Hash(newRaw);

        // The atomic claim: only the first concurrent request can flip this
        // token from "live" to "replaced". Everyone else gets 0 affected rows.
        var claimed = await _db.RefreshTokens
            .Where(t => t.TokenHash == hash && t.ReplacedAt == null && t.RevokedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(t => t.ReplacedByHash, newHash)
                    .SetProperty(t => t.ReplacedAt, now),
                ct);

        if (claimed == 0)
        {
            // Lost the race (or the row changed between the read and the
            // update). Re-read and classify: a just-replaced token is a
            // harmless duplicate; an older one is a replay.
            // AsNoTracking: the row is already tracked from the first read, and
            // EF Core returns the tracked instance without refreshing its
            // values — a tracked re-read would see the stale pre-claim state
            // (ReplacedAt = NULL) and misclassify the duplicate as a replay.
            var fresh = await _db.RefreshTokens
                .AsNoTracking()
                .Include(t => t.User)
                .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
            if (fresh is null || fresh.RevokedAt is not null || fresh.ExpiresAt <= now)
            {
                return new RefreshRotationResult(RefreshRotationOutcome.Invalid, null, null);
            }

            return await ClassifyReplayAsync(fresh, now, ct);
        }

        _db.RefreshTokens.Add(new RefreshToken
        {
            TokenHash = newHash,
            UserId = row.UserId,
            FamilyId = row.FamilyId,
            CreatedAt = now,
            ExpiresAt = now.AddDays(_options.ExpiryDays),
        });
        await _db.SaveChangesAsync(ct);

        return new RefreshRotationResult(RefreshRotationOutcome.Success, newRaw, row.User);
    }

    /// <summary>
    /// Revokes the rotation family the given raw token belongs to (logout).
    /// Returns true when a live family was revoked.
    /// </summary>
    public async Task<bool> RevokeFamilyByTokenAsync(string rawToken, CancellationToken ct)
    {
        var row = await _db.RefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == Hash(rawToken), ct);
        if (row is null || row.RevokedAt is not null)
        {
            return false;
        }

        await RevokeFamilyAsync(row, ct);
        return true;
    }

    /// <summary>
    /// Deletes refresh-token rows that are dead (revoked or expired) in
    /// bounded batches. Live rows — including replaced-but-within-grace ones
    /// (needed to answer duplicates) — are kept until they expire.
    /// Returns the number of deleted rows.
    /// </summary>
    public async Task<int> CleanupExpiredAndRevokedAsync(int? batchSize = null, CancellationToken ct = default)
    {
        var batch = batchSize ?? _options.CleanupBatchSize;
        var total = 0;

        while (true)
        {
            var now = DateTime.UtcNow;
            var rows = await _db.RefreshTokens
                .Where(t => t.RevokedAt != null || t.ExpiresAt < now)
                .OrderBy(t => t.Id)
                .Take(batch)
                .ToListAsync(ct);

            if (rows.Count == 0)
            {
                break;
            }

            _db.RefreshTokens.RemoveRange(rows);
            await _db.SaveChangesAsync(ct);
            total += rows.Count;

            if (rows.Count < batch)
            {
                break;
            }
        }

        return total;
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

    /// <summary>
    /// A replay of a replaced token. Inside the grace window it is a duplicate
    /// (401, no revocation); outside it, the family is revoked (theft).
    /// The warning log carries the family and user ids — never the token.
    /// </summary>
    private async Task<RefreshRotationResult> ClassifyReplayAsync(
        RefreshToken row,
        DateTime now,
        CancellationToken ct)
    {
        // Never revoke a family without positive evidence that the token was
        // replaced: a NULL ReplacedAt is not a replay (defensive; both call
        // sites normally guarantee a non-null value).
        if (row.ReplacedAt is null)
        {
            _logger.LogWarning(
                "Cannot classify refresh token for family {FamilyId} of user {UserId}: replaced-at is missing; the family is kept",
                row.FamilyId, row.UserId);
            return new RefreshRotationResult(RefreshRotationOutcome.Invalid, null, null);
        }

        if (now - row.ReplacedAt.Value <= _options.ReuseGracePeriod)
        {
            _logger.LogDebug(
                "Duplicate refresh request for a recently replaced token (family {FamilyId}, user {UserId}); the family is kept",
                row.FamilyId, row.UserId);
            return new RefreshRotationResult(RefreshRotationOutcome.Duplicate, null, null);
        }

        var revoked = await RevokeFamilyAsync(row, ct);
        _logger.LogWarning(
            "Refresh token reuse detected: rotation family {FamilyId} of user {UserId} revoked ({Count} tokens)",
            row.FamilyId, row.UserId, revoked);
        return new RefreshRotationResult(RefreshRotationOutcome.Reused, null, null);
    }

    private async Task<int> RevokeFamilyAsync(RefreshToken token, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return await _db.RefreshTokens
            .Where(t => t.FamilyId == token.FamilyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }
}
