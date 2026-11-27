using Xunit;
using Microsoft.IdentityModel.Tokens;
using System.Net;

namespace LoginAndReg.Tests;

/// <summary>
/// Unknown-kid throttling (step 4.3a): when a token carries a kid that is
/// not in the JWKS document, the provider re-fetches the document at most
/// once per RefreshThrottleSeconds (30 s in the factory). Two consecutive
/// requests with the unknown kid must cause exactly one fetch.
/// </summary>
public sealed class JwksThrottleTests : IAsyncLifetime
{
    private const string UnknownKid = "kid-not-in-jwks";

    private readonly JwksTestServer _jwks;
    private readonly MonolithAppFactory _factory;
    private readonly RsaSecurityKey _key;

    public JwksThrottleTests()
    {
        // A real key is served so the document fetch succeeds; the tokens
        // simply use a kid the document does not contain.
        _key = TestTokens.NewRsaKey();
        _jwks = JwksTestServer.Start(JwksTestServer.BuildJwks(_key, "kid-present"));
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
    public async Task UnknownKid_FetchesJwksAtMostOnceWithinThrottleWindow()
    {
        var key2 = TestTokens.NewRsaKey();
        var token = TestTokens.MakeRs256(key2, UnknownKid, sub: "42", login: "authuser");

        var hitsBefore = _jwks.HitCount;

        var first = await ApiHelpers.Send(_factory, HttpMethod.Get, "/api/me", jwtToken: token);
        var second = await ApiHelpers.Send(_factory, HttpMethod.Get, "/api/me", jwtToken: token);

        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);

        // One document fetch served both requests (the second was throttled),
        // never one fetch per request.
        Assert.Equal(hitsBefore + 1, _jwks.HitCount);
    }

    [Fact]
    public async Task KnownKid_CachesKey_NoRepeatFetches()
    {
        // After the key for kid 'kid-present' is cached, further tokens with
        // the same kid must not trigger another document fetch.
        var token = TestTokens.MakeRs256(_key, "kid-present", sub: "42", login: "authuser");

        var first = await ApiHelpers.Send(_factory, HttpMethod.Get, "/api/me", jwtToken: token);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var hitsAfterFirst = _jwks.HitCount;
        var second = await ApiHelpers.Send(_factory, HttpMethod.Get, "/api/me", jwtToken: token);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        Assert.Equal(hitsAfterFirst, _jwks.HitCount);
    }
}
