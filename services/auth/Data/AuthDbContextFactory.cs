using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Auth.Data;

/// <summary>
/// Design-time factory so <c>dotnet ef migrations add …</c> can build the model
/// without the running application (no live DB connection, no RSA keys, no
/// environment). The connection string here is a placeholder — it is never
/// opened during migration generation.
/// </summary>
public sealed class AuthDbContextFactory : IDesignTimeDbContextFactory<AuthDbContext>
{
    public AuthDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=auth;Username=auth;Password=auth")
            .Options;
        return new AuthDbContext(options);
    }
}
