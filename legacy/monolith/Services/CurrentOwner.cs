using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace LoginAndReg.Services
{
    /// <summary>
    /// File-owner resolution (step 4.3a, ADR 0018). The owner is a string
    /// (the TEXT <c>UserFiles.user_id</c>):
    ///
    /// - RS256 tokens from the Auth service: the owner is the <c>sub</c>
    ///   claim (the Auth user id) — no lookup in the monolith <c>Users</c>
    ///   table, which does not contain Auth users after the switch.
    /// - legacy HS256 tokens issued by the monolith: the owner is resolved
    ///   from the <c>name</c> claim (login) through the <c>Users</c> table,
    ///   as before, and returned as the numeric id in a string.
    ///
    /// Returns null when no owner can be determined; callers must treat the
    /// request as unauthenticated (401), not as an anonymous owner.
    /// </summary>
    public static class CurrentOwner
    {
        /// <summary>The raw JWT subject claim name.</summary>
        public const string SubClaim = "sub";

        /// <summary>
        /// Where <c>sub</c> ends up after the JWT handler's default inbound
        /// claim mapping — the documented fallback when the handler mapped
        /// the claim (the mapping is handler-level and not configurable
        /// through TokenValidationParameters in .NET 8).
        /// </summary>
        public const string MappedSubClaim = ClaimTypes.NameIdentifier;

        /// <summary>
        /// The login claim name the Auth service writes into its RS256
        /// tokens (see services/auth JwtTokenService).
        /// </summary>
        public const string UniqueNameClaim = "unique_name";

        /// <summary>
        /// Where <c>unique_name</c> ends up after the JWT handler's default
        /// inbound claim mapping (the raw name is replaced by this URI).
        /// </summary>
        public const string MappedUniqueNameClaim =
            "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/uniqueName";

        public static async Task<string?> ResolveAsync(
            AppDbContext db, ClaimsPrincipal principal, CancellationToken cancellationToken)
        {
            var sub = principal.FindFirstValue(SubClaim) ?? principal.FindFirstValue(MappedSubClaim);
            if (!string.IsNullOrWhiteSpace(sub))
            {
                return sub;
            }

            var login = principal.FindFirstValue(ClaimTypes.Name);
            if (string.IsNullOrWhiteSpace(login))
            {
                return null;
            }

            return await db.Users.AsNoTracking()
                .Where(u => u.login == login)
                .Select(u => u.id.ToString())
                .FirstOrDefaultAsync(cancellationToken);
        }
    }
}
