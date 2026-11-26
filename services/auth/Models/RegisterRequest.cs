using System.ComponentModel.DataAnnotations;

namespace Auth.Models;

/// <summary>
/// POST /api/auth/register body. Same fields as the monolith's register
/// contract; the password policy (min length + common-passwords list) is
/// enforced server-side (ADR 0018).
/// </summary>
public sealed class RegisterRequest
{
    [Required]
    [StringLength(64)]
    public string? Login { get; set; }

    [Required]
    [StringLength(128)]
    public string? Password { get; set; }

    [StringLength(128)]
    public string? FullName { get; set; }

    public DateOnly? BirthDate { get; set; }
}
