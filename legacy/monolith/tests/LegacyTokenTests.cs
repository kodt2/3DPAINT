using Xunit;
using System.Net;
using System.Text.Json;

namespace LoginAndReg.Tests;

/// <summary>
/// Legacy HS256 monolith tokens (step 4.3a): the old flow must keep working,
/// and the new iss/aud validation must reject tampered tokens. The JWKS URL
/// points at a dead port — none of these tests may require the Auth service
/// (the monolith must serve HS256 traffic with Auth fully down).
/// </summary>
public sealed class LegacyTokenTests : IAsyncLifetime
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
    public async Task Health_Returns200_WhenAuthIsDown()
    {
        var response = await ApiHelpers.Send(_factory, HttpMethod.Get, "/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithoutCookie_Returns401()
    {
        var response = await ApiHelpers.Send(_factory, HttpMethod.Get, "/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task LegacyLoginFlow_CookieGrantsAccess()
    {
        var login = ApiHelpers.UniqueLogin();
        var password = "Str0ng-Passw0rd!";

        // Register (the legacy monolith still issues the HS256 cookie).
        var registered = await ApiHelpers.Send(_factory, HttpMethod.Post, "/api/auth/register",
            new { fullName = "Test User", birthDate = new DateOnly(1990, 1, 1), login, password });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        // Login sets the jwt cookie.
        var loginResponse = await ApiHelpers.Send(_factory, HttpMethod.Post, "/api/auth/login",
            new { login, password });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        var setCookie = loginResponse.Headers.GetValues("Set-Cookie").First();
        var cookieValue = ExtractCookieValue(setCookie, "jwt");
        Assert.False(string.IsNullOrWhiteSpace(cookieValue));

        // /api/me with the cookie: 200 and the login round-trips.
        var me = await ApiHelpers.Send(_factory, HttpMethod.Get, "/api/me", jwtToken: cookieValue);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var meBody = await me.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(meBody);
        Assert.Equal(login, doc.RootElement.GetProperty("login").GetString());

        // The models list answers too.
        var models = await ApiHelpers.Send(_factory, HttpMethod.Get, "/api/models", jwtToken: cookieValue);
        Assert.Equal(HttpStatusCode.OK, models.StatusCode);
    }

    [Theory]
    [InlineData("3dpaint.wrong-issuer")] // wrong issuer
    [InlineData(MonolithAppFactory.JwtIssuer)] // right issuer, wrong audience below
    public async Task Hs256_WrongIssuerOrAudience_Returns401(string issuer)
    {
        var token = issuer == MonolithAppFactory.JwtIssuer
            ? TestTokens.MakeHs256(MonolithAppFactory.JwtKey, "someone",
                audience: "3dpaint.wrong-audience")
            : TestTokens.MakeHs256(MonolithAppFactory.JwtKey, "someone", issuer: issuer);

        var response = await ApiHelpers.Send(_factory, HttpMethod.Get, "/api/me", jwtToken: token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Hs256_WrongKey_Returns401()
    {
        var token = TestTokens.MakeHs256("another-secret-key-that-is-long-enough-0123456789", "someone");
        var response = await ApiHelpers.Send(_factory, HttpMethod.Get, "/api/me", jwtToken: token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Hs256_ExpiredToken_Returns401()
    {
        var token = TestTokens.MakeHs256(MonolithAppFactory.JwtKey, "someone",
            expires: DateTimeOffset.UtcNow.AddMinutes(-5));
        var response = await ApiHelpers.Send(_factory, HttpMethod.Get, "/api/me", jwtToken: token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Hs256_ValidToken_ForUnknownUser_Returns401()
    {
        // Signature/iss/aud/expiry all valid, but the login does not exist in
        // the monolith Users table: the legacy /api/me contract is 401.
        var token = TestTokens.MakeHs256(MonolithAppFactory.JwtKey, ApiHelpers.UniqueLogin());
        var response = await ApiHelpers.Send(_factory, HttpMethod.Get, "/api/me", jwtToken: token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    internal static string ExtractCookieValue(string setCookie, string name)
    {
        var first = setCookie.Split(';')[0];
        var idx = first.IndexOf('=');
        if (idx < 0)
        {
            return string.Empty;
        }

        var key = first[..idx].Trim();
        return key == name ? first[(idx + 1)..].Trim() : string.Empty;
    }
}
