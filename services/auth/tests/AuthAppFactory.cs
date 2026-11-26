using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Auth.Tests;

/// <summary>
/// In-process host for the Auth service (<c>WebApplicationFactory</c>, ADR 0018 —
/// compose-stand strategy). The app reads its configuration (the Postgres
/// connection string, the JWT key paths) from the environment variables set by
/// the test stand (infra/compose.auth-test.yml), so the only override here is
/// forcing <c>Development</c>: the RSA signing keys are then generated into a
/// temp directory on first start, and the in-memory server runs over the
/// already-migrated test database.
/// </summary>
public sealed class AuthAppFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
    }
}

/// <summary>
/// One shared app instance — and therefore one RSA key and one migrated schema —
/// for the whole suite (xUnit collection fixture).
/// </summary>
[CollectionDefinition(nameof(AuthAppCollection))]
public sealed class AuthAppCollection : ICollectionFixture<AuthAppFactory>
{
    // No code: its purpose is to carry [CollectionDefinition] so every
    // [Collection(nameof(AuthAppCollection))] test shares a single factory.
}
