namespace Auth.Models;

/// <summary>
/// The auth user as seen by the SPA. Same shape as the monolith's
/// <c>AuthUserDto</c> (<c>login</c>, <c>fullName</c>, <c>birthDate</c>) plus the
/// stable <c>id</c> — the version-7 Guid user key as a canonical lower-case
/// string (step 4.3a.1).
/// </summary>
public sealed record AuthUserDto(
    string Id,
    string? Login,
    string? FullName,
    DateOnly? BirthDate);

/// <summary>Maps an entity to the SPA-facing DTO.</summary>
public static class AuthUserDtoMap
{
    public static AuthUserDto ToDto(Entities.AuthUser user)
        => new(user.Id.ToString(), user.UserName, user.FullName, user.BirthDate);
}
