namespace Auth.Models;

/// <summary>
/// The auth user as seen by the SPA. Same shape as the monolith's
/// <c>AuthUserDto</c> (<c>login</c>, <c>fullName</c>, <c>birthDate</c>) plus the
/// stable <c>id</c> (added in step 4.1; the monolith's id stays int, so the
/// generated SPA client stays compatible — see the step report).
/// </summary>
public sealed record AuthUserDto(
    int Id,
    string? Login,
    string? FullName,
    DateOnly? BirthDate);

/// <summary>Maps an entity to the SPA-facing DTO.</summary>
public static class AuthUserDtoMap
{
    public static AuthUserDto ToDto(Entities.AuthUser user)
        => new(user.Id, user.UserName, user.FullName, user.BirthDate);
}
