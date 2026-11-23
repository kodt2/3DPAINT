using System.ComponentModel.DataAnnotations;

namespace LoginAndReg.Models.Api
{
    /// <summary>
    /// Body for POST /api/auth/register. Validation rules mirror the legacy
    /// RegistrationViewModel (Razor form) so both entry points agree.
    /// </summary>
    public sealed record RegisterRequest
    {
        [Required]
        [StringLength(100)]
        public string FullName { get; init; } = string.Empty;

        [Required]
        public DateOnly BirthDate { get; init; }

        [Required]
        [StringLength(50)]
        public string Login { get; init; } = string.Empty;

        [Required]
        [StringLength(100, MinimumLength = 6)]
        public string Password { get; init; } = string.Empty;
    }
}
