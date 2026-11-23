using System.ComponentModel.DataAnnotations;

namespace LoginAndReg.Models.Api
{
    /// <summary>
    /// Body for POST /api/auth/login. The login is an arbitrary string (not
    /// necessarily an e-mail) — the same rule as the legacy Razor form.
    /// </summary>
    public sealed record LoginRequest
    {
        [Required]
        public string Login { get; init; } = string.Empty;

        [Required]
        public string Password { get; init; } = string.Empty;
    }
}
