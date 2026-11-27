using Xunit;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text.Json;

namespace LoginAndReg.Tests;

/// <summary>
/// Auth RS256 tokens (step 4.3a): the monolith validates tokens issued by
/// the Auth service against its JWKS endpoint (lazy fetch, kid matching).
/// Full validation contract: signature, issuer, audience and expiry.
/// </summary>
public sealed class Rs256TokenTests : IAsyncLifetime
{
    private const string Kid = "monolith-test-key-1";

    private readonly JwksTestServer _jwks;
    private readonly MonolithAppFactory _factory;
    private readonly RsaSecurityKey _key;

    public Rs256TokenTests()
    {
        _key = TestTokens.NewRsaKey();
        _jwks = JwksTestServer.Start(JwksTestServer.BuildJwks(_key, Kid));
        _factory = new MonolithAppFactory(_jwks.Url);
    }

    public async Task InitializeAsync()
    {
        // .NET 8 WebApplicationFactory has no public InitializeAsync: force
        // startup with a probe so configuration errors surface here, not in
        // the middle of a test.
        using var client = _factory.CreateClient();
        await client.GetAsync("/api/health");
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _jwks.DisposeAsync();
    }

    [Fact]
    public async Task Rs256_ValidToken_IsAccepted()
    {
        var token = TestTokens.MakeRs256(_key, Kid, sub: "42", login: "authuser");

        var response = await ApiHelpers.Send(_factory, HttpMethod.Get, "/api/me", jwtToken: token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        // The RS256 path resolves the identity from the token only (no
        // monolith Users/Persons join): the login comes from unique_name and
        // the profile fields are null (they belong to the Auth service).
        Assert.Equal("authuser", doc.RootElement.GetProperty("login").GetString());
        Assert.Equal(JsonValueKind.Null,
            doc.RootElement.GetProperty("fullName").ValueKind);
    }

    [Fact]
    public async Task Rs256_WrongSignature_SameKid_Returns401()
    {
        // The header kid matches the JWKS entry, but the payload is signed
        // with a different key: the key resolved by kid must not verify it.
        var otherKey = TestTokens.NewRsaKey();
        var token = TestTokens.MakeRs256(otherKey, Kid, sub: "42", login: "authuser");

        var response = await ApiHelpers.Send(_factory, HttpMethod.Get, "/api/me", jwtToken: token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rs256_WrongIssuer_Returns401()
    {
        var token = TestTokens.MakeRs256(_key, Kid, sub: "42", login: "authuser", issuer: "3dpaint.wrong");
        var response = await ApiHelpers.Send(_factory, HttpMethod.Get, "/api/me", jwtToken: token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rs256_WrongAudience_Returns401()
    {
        var token = TestTokens.MakeRs256(_key, Kid, sub: "42", login: "authuser", audience: "3dpaint.wrong");
        var response = await ApiHelpers.Send(_factory, HttpMethod.Get, "/api/me", jwtToken: token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rs256_ExpiredToken_Returns401()
    {
        var token = TestTokens.MakeRs256(_key, Kid, sub: "42", login: "authuser",
            expires: DateTimeOffset.UtcNow.AddMinutes(-5));
        var response = await ApiHelpers.Send(_factory, HttpMethod.Get, "/api/me", jwtToken: token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rs256_MissingKid_Returns401()
    {
        // A valid RS256 token without a kid header cannot be matched against
        // any JWKS entry and must be rejected (no key guessing).
        var token = MakeRs256WithoutKid();
        var response = await ApiHelpers.Send(_factory, HttpMethod.Get, "/api/me", jwtToken: token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private string MakeRs256WithoutKid()
    {
        var handler = new JwtSecurityTokenHandler();
        var now = DateTimeOffset.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new System.Security.Claims.ClaimsIdentity(new[]
            {
                new System.Security.Claims.Claim("sub", "42"),
                new System.Security.Claims.Claim("unique_name", "authuser"),
            }),
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
            Issuer = MonolithAppFactory.AuthIssuer,
            Audience = MonolithAppFactory.AuthAudience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.AddMinutes(5).UtcDateTime,
        };

        return handler.WriteToken(handler.CreateToken(descriptor)!);
    }
}
