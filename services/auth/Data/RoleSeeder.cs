using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Auth.Data;

/// <summary>
/// Seeds the default <c>User</c> role. Run by the one-shot migrate container
/// (after the migrations are applied) and by the test harness. Idempotent.
/// Role management endpoints are out of scope for step 4.1 (the data model is
/// in place, per the plan).
/// </summary>
public static class RoleSeeder
{
    public const string DefaultRole = "User";

    public static async Task SeedAsync(AuthDbContext db, CancellationToken ct = default)
    {
        if (!await db.Roles.AnyAsync(r => r.Name == DefaultRole, ct))
        {
            // NormalizedName must match UserManager's normalization (upper-case
            // by default) — AddToRoleAsync looks the role up by it.
            db.Roles.Add(new IdentityRole<Guid>
            {
                Name = DefaultRole,
                NormalizedName = DefaultRole.ToUpperInvariant(),
            });
            await db.SaveChangesAsync(ct);
        }
    }
}
