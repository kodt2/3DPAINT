namespace Auth.Entities;

/// <summary>
/// A refresh token (opaque rotation family — step 4.2 rotates and detects
/// reuse; ADR 0018). Only the SHA-256 hash of the token is stored: the raw
/// value exists solely in the browser's http-only <c>rt</c> cookie and in
/// memory during a request. The schema is created in step 4.1; the rotation
/// / reuse-detection logic lands in step 4.2.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>SHA-256 (lower-case hex) of the raw token. Uniquely indexed.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Owner: the user's version-7 Guid key (step 4.3a.1).</summary>
    public Guid UserId { get; set; }

    public AuthUser? User { get; set; }

    /// <summary>
    /// Rotation family: every token issued from a single login/register shares
    /// a <c>FamilyId</c>. Reuse of a replaced token revokes the whole family (4.2).
    /// </summary>
    public Guid FamilyId { get; set; } = Guid.NewGuid();

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    /// <summary>Set when the token is revoked (logout / rotation / reuse detection).</summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>Hash of the token that replaced this one (the rotation chain).</summary>
    public string? ReplacedByHash { get; set; }

    /// <summary>
    /// When the token was consumed by a rotation (step 4.2). A replay of a
    /// replaced token inside the grace window is a harmless duplicate (e.g. a
    /// double-fired browser request); a replay outside the window is treated
    /// as theft and revokes the whole family.
    /// </summary>
    public DateTime? ReplacedAt { get; set; }
}
