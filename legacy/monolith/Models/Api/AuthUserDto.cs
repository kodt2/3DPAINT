namespace LoginAndReg.Models.Api
{
    /// <summary>
    /// Authenticated user as seen by the SPA. Matches the /api/me shape
    /// (login is the identity; fullName/birthDate come from the Person row).
    /// </summary>
    public sealed record AuthUserDto(
        string Login,
        string? FullName,
        DateOnly? BirthDate);
}
