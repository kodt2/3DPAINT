using Xunit;
using System.Net;

namespace LoginAndReg.Tests;

/// <summary>
/// JWKS unavailable (Auth down): the monolith must still start and answer
/// /api/health, and protected endpoints must return a clean 401 for RS256
/// tokens — never a 500.
/// </summary>
public sealed class JwksUnavailableTests : IAsyncLifetime
{
    private const string DeadJwksUrl = "http://127.0.0.1:9/.well-known/jwks.json";

    // The Auth service issues the user id as a canonical lower-case GUID
    // string in the sub claim (step 4.3a.1).
    private const string Sub = "01970000-0000-7000-8000-000000000001";

    private readonly MonolithAppFactory _factory = new(DeadJwksUrl);

    public async Task InitializeAsync()
    {
        // .NET 8 WebApplicationFactory has no public InitializeAsync: force
        // startup with a probe so configuration errors surface here, not in
        // the middle of a test.
        using var client = _factory.CreateClient();
        await client.GetAsync("/api/health");
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Health_Returns200_WithDeadJwks()
    {
        var response = await ApiHelpers.Send(_factory, HttpMethod.Get, "/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Rs256_WithDeadJwks_Returns401_Not500()
    {
        var key = TestTokens.NewRsaKey();
        var token = TestTokens.MakeRs256(key, "kid-dead", sub: Sub, login: "authuser");

        var response = await ApiHelpers.Send(_factory, HttpMethod.Get, "/api/me", jwtToken: token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MalformedToken_Returns401_Not500()
    {
        var response = await ApiHelpers.Send(_factory, HttpMethod.Get, "/api/me",
            jwtToken: "not-a-jwt-at-all");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
