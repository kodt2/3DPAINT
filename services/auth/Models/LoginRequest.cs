using System.ComponentModel.DataAnnotations;

namespace Auth.Models;

/// <summary>
/// POST /api/auth/login body. Same fields as the monolith's login contract.
/// </summary>
public sealed class LoginRequest
{
    [Required]
    [StringLength(64)]
    public string? Login { get; set; }

    [Required]
    [StringLength(128)]
    public string? Password { get; set; }
}
