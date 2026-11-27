using System.Security.Claims;
using LoginAndReg.Models.Api;
using LoginAndReg.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LoginAndReg.Controllers;

/// <summary>
/// Minimal JSON endpoints for the SPA (step 3a). These /api endpoints are
/// the contract the SPA consumes (the Razor UI was removed in step 3d.2).
/// </summary>
[ApiController]
[Authorize]
[Route("api")]
public class ApiStatusController : ControllerBase
{
    private readonly AppDbContext _context;

    public ApiStatusController(AppDbContext context)
    {
        _context = context;
    }

    /// <summary>Public connectivity probe used by the SPA connection check.</summary>
    [HttpGet("health")]
    [AllowAnonymous]
    public IActionResult Health()
    {
        return Ok(new { status = "ok", service = "monolith" });
    }

    /// <summary>
    /// Current user from the auth cookie. 401 when the cookie is absent or
    /// stale. The response body is the AuthUserDto contract (same shape as
    /// the /api/auth/* login/register responses) — the SPA types it from
    /// OpenAPI instead of an anonymous object.
    /// </summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(AuthUserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        var sub = User.FindFirstValue(CurrentOwner.SubClaim)
            ?? User.FindFirstValue(CurrentOwner.MappedSubClaim);
        var login = User.FindFirstValue(ClaimTypes.Name)
            ?? User.FindFirstValue(CurrentOwner.UniqueNameClaim)
            ?? User.FindFirstValue(CurrentOwner.MappedUniqueNameClaim);

        if (string.IsNullOrWhiteSpace(login))
        {
            return Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Unauthorized",
                detail: "Not authenticated");
        }

        // Auth RS256 tokens: the profile belongs to the Auth service, not to
        // this database. Return the login from the token only — no join into
        // Users/Persons, so a same-login legacy user cannot leak a profile
        // to an Auth user (step 4.3a; after the 4.3b switch this endpoint is
        // served by the Auth service directly).
        if (!string.IsNullOrWhiteSpace(sub))
        {
            return Ok(new AuthUserDto(login, null, null));
        }

        // Legacy HS256 tokens: unchanged behavior — the user must exist in
        // this database.
        var user = await _context.Users
            .Where(u => u.login == login)
            .Select(u => (int?)u.id)
            .SingleOrDefaultAsync(cancellationToken);
        if (user is null)
        {
            return Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Unauthorized",
                detail: "User not found");
        }

        var person = await _context.Persons
            .Where(p => p.user_id == user)
            .Select(p => new { p.full_name, p.birth_date })
            .FirstOrDefaultAsync(cancellationToken);

        AuthUserDto dto = new(login, person?.full_name, person?.birth_date);
        return Ok(dto);
    }
}
