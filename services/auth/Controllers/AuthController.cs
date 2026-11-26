using Auth.Entities;
using Auth.Models;
using Auth.Options;
using Auth.Security;
using Auth.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Auth.Controllers;

/// <summary>
/// JSON auth endpoints for the SPA (ADR 0011 / ADR 0018). This is the
/// <c>/api/auth/*</c> contract the SPA consumes; the response shapes match the
/// monolith's (login, fullName, birthDate) plus the added <c>id</c>.
///
/// Auth model: a "jwt" cookie (SameSite=Lax, HttpOnly, Secure) with an RS256
/// access token, plus an http-only "rt" cookie with an opaque refresh token.
/// Login and register log the user in immediately (the SPA has no separate
/// session bootstrap).
/// All endpoints are [AllowAnonymous]: login/register are public by design, and
/// logout is an idempotent no-op when anonymous.
/// </summary>
[ApiController]
[Route("api/auth")]
[AllowAnonymous]
public sealed class AuthController : ControllerBase
{
    private readonly IAuthService _auth;
    private readonly JwtTokenService _jwt;
    private readonly RefreshTokenService _refresh;
    private readonly RefreshTokenOptions _refreshOptions;
    private readonly TimeSpan _jwtLifetime;

    public AuthController(
        IAuthService auth,
        JwtTokenService jwt,
        RefreshTokenService refresh,
        IOptions<RefreshTokenOptions> refreshOptions,
        IOptions<JwtOptions> jwtOptions)
    {
        _auth = auth;
        _jwt = jwt;
        _refresh = refresh;
        _refreshOptions = refreshOptions.Value;
        _jwtLifetime = TimeSpan.FromMinutes(jwtOptions.Value.ExpiryMinutes);
    }

    /// <summary>POST /api/auth/login — validates credentials and sets the auth cookies.</summary>
    [HttpPost("login")]
    [ProducesResponseType(typeof(AuthUserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        var user = await _auth.LoginAsync(request, cancellationToken);
        if (user is null)
        {
            // Uniform 401 for unknown login AND wrong password (ADR 0018): the
            // response is identical either way, so logins cannot be enumerated.
            return Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Unauthorized",
                detail: PasswordPolicyOptions.LoginFailureMessage);
        }

        await IssueSessionAsync(user, cancellationToken);
        return Ok(AuthUserDtoMap.ToDto(user));
    }

    /// <summary>
    /// POST /api/auth/register — creates the user and logs them in. 409 when the
    /// login is taken; 400 when the password fails the policy.
    /// </summary>
    [HttpPost("register")]
    [ProducesResponseType(typeof(AuthUserDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request, CancellationToken cancellationToken)
    {
        AuthUser user;
        try
        {
            user = await _auth.RegisterAsync(request, cancellationToken);
        }
        catch (DuplicateLoginException ex)
        {
            return Conflict(new ProblemDetails
            {
                Title = "Conflict",
                Status = StatusCodes.Status409Conflict,
                Detail = ex.Message,
            });
        }
        catch (WeakPasswordException ex)
        {
            return PasswordProblem(ex.Message);
        }

        await IssueSessionAsync(user, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, AuthUserDtoMap.ToDto(user));
    }

    /// <summary>
    /// POST /api/auth/refresh — rotates the session: the <c>rt</c> cookie is
    /// consumed atomically and a fresh access + refresh pair is issued. Every
    /// failure (missing/garbage/expired/revoked/reused token) is the same 401
    /// ProblemDetails, so the SPA simply falls back to the login screen.
    /// </summary>
    [HttpPost("refresh")]
    [ProducesResponseType(typeof(AuthUserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh(CancellationToken cancellationToken)
    {
        var raw = Request.Cookies[AuthCookies.RefreshName];
        if (string.IsNullOrEmpty(raw))
        {
            return SessionExpired();
        }

        var result = await _refresh.RotateAsync(raw, cancellationToken);
        if (result.Outcome is not RefreshRotationOutcome.Success || result.User is null)
        {
            return SessionExpired();
        }

        var accessToken = _jwt.CreateAccessToken(result.User.Id, result.User.UserName!);
        Response.Cookies.Append(AuthCookies.JwtName, accessToken, AuthCookies.Jwt(_jwtLifetime));
        Response.Cookies.Append(
            AuthCookies.RefreshName,
            result.NewRawToken!,
            AuthCookies.Refresh(_refreshOptions.CookiePath, TimeSpan.FromDays(_refreshOptions.ExpiryDays)));

        return Ok(AuthUserDtoMap.ToDto(result.User));
    }

    /// <summary>
    /// POST /api/auth/logout — revokes the refresh-token family and removes the
    /// auth cookies (idempotent). Revoking the family makes any <c>rt</c> the
    /// client still holds unusable, not just the cookies the client deletes.
    /// </summary>
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        var raw = Request.Cookies[AuthCookies.RefreshName];
        if (!string.IsNullOrEmpty(raw))
        {
            await _refresh.RevokeFamilyByTokenAsync(raw, cancellationToken);
        }

        DeleteCookie(AuthCookies.JwtName, AuthCookies.DeletedJwt());
        DeleteCookie(AuthCookies.RefreshName, AuthCookies.DeletedRefresh(_refreshOptions.CookiePath));
        return NoContent();
    }

    // Expire the cookie: Max-Age=0 is the standard deletion signal, and the same
    // Path/Secure/SameSite flags as when it was set, so the browser matches it.
    // (Response.Cookies.Delete only sets Expires and drops MaxAge, which some
    // browsers ignore — so we Append an empty value with Max-Age=0 directly.)
    private void DeleteCookie(string name, CookieOptions options)
    {
        options.MaxAge = TimeSpan.Zero;
        options.Expires = DateTime.MinValue;
        Response.Cookies.Append(name, string.Empty, options);
    }

    /// <summary>
    /// Sets the RS256 access-token cookie and issues an opaque refresh token in
    /// the http-only "rt" cookie. All cookie attributes come from AuthCookies
    /// (step 4.2), the single source of truth for the auth cookie flags.
    /// </summary>
    private async Task IssueSessionAsync(AuthUser user, CancellationToken cancellationToken)
    {
        var accessToken = _jwt.CreateAccessToken(user.Id, user.UserName!);
        Response.Cookies.Append(AuthCookies.JwtName, accessToken, AuthCookies.Jwt(_jwtLifetime));

        var rawRefresh = await _refresh.CreateForUserAsync(user.Id, cancellationToken);
        Response.Cookies.Append(
            AuthCookies.RefreshName,
            rawRefresh,
            AuthCookies.Refresh(_refreshOptions.CookiePath, TimeSpan.FromDays(_refreshOptions.ExpiryDays)));
    }

    /// <summary>
    /// The uniform 401 for every /refresh failure. One message on purpose:
    /// the client (and an attacker) must not be able to tell which kind of
    /// token problem happened (ADR 0018).
    /// </summary>
    private IActionResult SessionExpired()
        => Problem(
            statusCode: StatusCodes.Status401Unauthorized,
            title: "Unauthorized",
            detail: "Сессия истекла. Войдите снова.");

    private static IActionResult PasswordProblem(string message)
    {
        var details = new ValidationProblemDetails(
                new Dictionary<string, string[]> { ["Password"] = [message] })
        {
            Title = "One or more validation errors occurred.",
            Status = StatusCodes.Status400BadRequest,
            Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
        };
        return new BadRequestObjectResult(details);
    }
}
