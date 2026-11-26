using Microsoft.AspNetCore.Identity;

namespace Auth.Entities;

/// <summary>
/// Auth service user: ASP.NET Identity user with an integer id, the login in
/// <see cref="IdentityUser.UserName"/> and a PBKDF2 <see cref="IdentityUser.PasswordHash"/>
/// (ADR 0018 — PBKDF2, not the monolith's SHA-256+salt), plus the profile
/// fields the SPA needs (<c>fullName</c>, <c>birthDate</c>).
/// </summary>
public class AuthUser : IdentityUser<int>
{
    public string? FullName { get; set; }

    public DateOnly? BirthDate { get; set; }
}
