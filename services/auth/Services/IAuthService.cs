using Auth.Entities;
using Auth.Models;

namespace Auth.Services;

/// <summary>
/// Authentication operations: register, login and the current-user lookup.
/// Failure semantics are domain-level (exceptions / null) — the controller maps
/// them to HTTP responses (ADR 0018).
/// </summary>
public interface IAuthService
{
    /// <summary>
    /// Creates a user and assigns the default role.
    /// Throws <see cref="DuplicateLoginException"/> or <see cref="WeakPasswordException"/>.
    /// </summary>
    Task<AuthUser> RegisterAsync(RegisterRequest request, CancellationToken ct);

    /// <summary>Returns the user on success, or null for any invalid credential.</summary>
    Task<AuthUser?> LoginAsync(LoginRequest request, CancellationToken ct);

    /// <summary>
    /// Returns the user by id (the JWT <c>sub</c> — the version-7 Guid key as
    /// a string), or null if not found.
    /// </summary>
    Task<AuthUser?> GetByIdAsync(Guid id, CancellationToken ct);
}
