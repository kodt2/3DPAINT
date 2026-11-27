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
        var token = TestTokens.MakeRs256(key, "kid-dead", sub: "42", login: "authuser");

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
