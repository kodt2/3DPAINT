using LoginAndReg.Models.Api;
using LoginAndReg.Models.Entities;
using LoginAndReg.Models.ViewModels;
using LoginAndReg.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LoginAndReg.Controllers
{
    /// <summary>
    /// JSON auth endpoints for the SPA (step 3c.1). The legacy Razor flow
    /// (/Account/Login, /Account/Register) keeps working unchanged; this
    /// controller only adds the /api/auth/* contract the SPA consumes.
    ///
    /// Auth model (ADR 0011): the same "jwt" cookie as the Razor flow
    /// (SameSite=Lax, HttpOnly, Secure). Login and register log the user in
    /// immediately (the SPA has no separate session bootstrap).
    /// All endpoints are [AllowAnonymous] at the class level: login/register
    /// are public by design, and logout is an idempotent no-op when anonymous.
    /// </summary>
    [ApiController]
    [Route("api/auth")]
    [Microsoft.AspNetCore.Authorization.AllowAnonymous]
    public class AuthApiController : ControllerBase
    {
        private readonly IAccountService _accountService;
        private readonly JwtService _jwtService;
        private readonly AppDbContext _context;

        public AuthApiController(IAccountService accountService, JwtService jwtService, AppDbContext context)
        {
            _accountService = accountService;
            _jwtService = jwtService;
            _context = context;
        }

        /// <summary>POST /api/auth/login — validates credentials and sets the auth cookie.</summary>
        [HttpPost("login")]
        [ProducesResponseType(typeof(AuthUserDto), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
        {
            var user = await _accountService.AuthenticateAsync(new LoginViewModel
            {
                Login = request.Login,
                Password = request.Password,
            });
            if (user is null)
            {
                return Problem(
                    statusCode: StatusCodes.Status401Unauthorized,
                    title: "Unauthorized",
                    detail: "Неверный логин или пароль");
            }

            SetAuthCookie(user.login);
            return Ok(await BuildUserDtoAsync(user, cancellationToken));
        }

        /// <summary>
        /// POST /api/auth/register — creates the user (and the Person row) and
        /// logs them in. 409 when the login is taken.
        /// </summary>
        [HttpPost("register")]
        [ProducesResponseType(typeof(AuthUserDto), StatusCodes.Status201Created)]
        [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
        public async Task<IActionResult> Register([FromBody] RegisterRequest request, CancellationToken cancellationToken)
        {
            var registered = await _accountService.RegisterAsync(new RegistrationViewModel
            {
                FullName = request.FullName,
                BirthDate = request.BirthDate.ToDateTime(TimeOnly.MinValue),
                Login = request.Login,
                Password = request.Password,
            });
            if (!registered)
            {
                return Conflict(new ProblemDetails
                {
                    Title = "Conflict",
                    Status = StatusCodes.Status409Conflict,
                    Detail = "Такой логин уже существует",
                });
            }

            var user = await _context.Users
                .FirstAsync(u => u.login == request.Login, cancellationToken);

            SetAuthCookie(user.login);
            return StatusCode(StatusCodes.Status201Created, await BuildUserDtoAsync(user, cancellationToken));
        }

        /// <summary>POST /api/auth/logout — removes the auth cookie (idempotent).</summary>
        [HttpPost("logout")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public IActionResult Logout()
        {
            Response.Cookies.Delete(AuthCookie.Name, AuthCookie.CreateOptions());
            return NoContent();
        }

        private void SetAuthCookie(string login)
        {
            Response.Cookies.Append(
                AuthCookie.Name,
                _jwtService.GenerateToken(login),
                AuthCookie.CreateOptions());
        }

        private async Task<AuthUserDto> BuildUserDtoAsync(User user, CancellationToken cancellationToken)
        {
            var person = await _context.Persons
                .Where(p => p.user_id == user.id)
                .Select(p => new { p.full_name, p.birth_date })
                .FirstOrDefaultAsync(cancellationToken);

            return new AuthUserDto(user.login, person?.full_name, person?.birth_date);
        }
    }
}
