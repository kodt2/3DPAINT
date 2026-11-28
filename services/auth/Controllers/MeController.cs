using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Auth.Models;
using Auth.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Auth.Controllers;

/// <summary>
/// Current-user endpoint. Serves <c>GET /api/me</c> — the exact route the SPA
/// calls (and the monolith serves). The response is the AuthUserDto contract
/// (login, fullName, birthDate) plus the added <c>id</c> (step 4.1).
///
/// The JWT carries <c>sub</c> = user id; the endpoint resolves the id (the
/// monolith read the login from the Name claim, but the id is the stable
/// identifier and both are present in the token).
/// </summary>
[ApiController]
[Authorize]
[Route("api")]
public sealed class MeController : ControllerBase
{
    private readonly IAuthService _auth;

    public MeController(IAuthService auth)
    {
        _auth = auth;
    }

    [HttpGet("me")]
    [ProducesResponseType(typeof(AuthUserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        var idClaim = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        // sub is the version-7 Guid user key as a lower-case string (4.3a.1).
        if (idClaim is null || !Guid.TryParse(idClaim, out var id))
        {
            return Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Unauthorized",
                detail: "Not authenticated");
        }

        var user = await _auth.GetByIdAsync(id, cancellationToken);
        if (user is null)
        {
            return Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Unauthorized",
                detail: "User not found");
        }

        return Ok(AuthUserDtoMap.ToDto(user));
    }
}
