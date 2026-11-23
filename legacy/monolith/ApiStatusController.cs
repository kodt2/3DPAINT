using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LoginAndReg.Controllers;

/// <summary>
/// Minimal JSON endpoints for the future SPA (step 3a). The Razor UI keeps using
/// /Account/* for now; these /api endpoints are the contract the SPA will consume.
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

    /// <summary>Current user from the auth cookie. 401 when the cookie is absent or stale.</summary>
    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        var login = User.FindFirstValue(ClaimTypes.Name);
        if (string.IsNullOrWhiteSpace(login))
        {
            return Unauthorized(new { message = "Not authenticated" });
        }

        var user = await _context.Users
            .Where(u => u.login == login)
            .Select(u => (int?)u.id)
            .SingleOrDefaultAsync(cancellationToken);
        if (user is null)
        {
            return Unauthorized(new { message = "User not found" });
        }

        var person = await _context.Persons
            .Where(p => p.user_id == user)
            .Select(p => new { p.full_name, p.birth_date })
            .FirstOrDefaultAsync(cancellationToken);

        return Ok(new
        {
            login = login,
            fullName = person?.full_name,
            birthDate = person?.birth_date,
        });
    }
}
