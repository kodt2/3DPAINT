using System.ComponentModel.DataAnnotations;
using Auth.Models.Validation;

namespace Auth.Models;

/// <summary>
/// Body for POST /api/auth/register. The field contract mirrors the
/// monolith's RegisterRequest 1:1 (step 4.2 correction): fullName and
/// birthDate are REQUIRED, with the same length limits and the same
/// DataAnnotations messages, so the SPA gets identical validation from
/// either service.
/// </summary>
public sealed class RegisterRequest
{
    [Required]
    [StringLength(100)]
    public string? FullName { get; set; }

    [Required]
    [BirthDateRange]
    public DateOnly? BirthDate { get; set; }

    [Required]
    [StringLength(50)]
    public string? Login { get; set; }

    [Required]
    [StringLength(100, MinimumLength = 6)]
    public string? Password { get; set; }
}
