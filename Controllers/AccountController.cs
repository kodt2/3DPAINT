using LoginAndReg.Models.ViewModels;
using LoginAndReg.Services;
using Microsoft.AspNetCore.Mvc;

namespace LoginAndReg.Controllers
{
    public class AccountController : Controller
    {
        private readonly JwtService _jwtService;
        private readonly IAccountService _accountService;

        public AccountController(JwtService jwtService, IAccountService accountService)
        {
            _jwtService = jwtService;
            _accountService = accountService;
        }

        [HttpGet]
        public IActionResult Auth()
        {
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> Login([FromForm] LoginViewModel model)
        {
            if (!ModelState.IsValid)
            {
                var errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage);
                return BadRequest(new { success = false, errors });
            }

            var user = await _accountService.AuthenticateAsync(model);
            if (user is null)
            {
                return Unauthorized(new { success = false, message = "Неверный логин или пароль" });
            }

            var token = _jwtService.GenerateToken(user.login);
            Response.Cookies.Append("jwt", token, new CookieOptions { HttpOnly = true, Secure = true });

            return Ok(new { success = true, redirectUrl = Url.Action("Index", "Home") });
        }

        [HttpPost]
        public async Task<IActionResult> Register([FromForm] RegistrationViewModel model)
        {
            if (!ModelState.IsValid)
            {
                var errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage);
                return BadRequest(new { success = false, errors });
            }

            var registered = await _accountService.RegisterAsync(model);
            if (!registered)
            {
                return Conflict(new { success = false, message = "Такой логин уже существует" });
            }

            return Ok(new { success = true, message = "Регистрация успешна!" });
        }
    }
}
