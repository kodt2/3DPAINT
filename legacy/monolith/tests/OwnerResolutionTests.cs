using Xunit;
using Microsoft.IdentityModel.Tokens;
using System.Net;
using System.Text.Json;

namespace LoginAndReg.Tests;

/// <summary>
/// File ownership with a string owner (step 4.3a): for RS256 tokens the
/// owner is the <c>sub</c> claim (an Auth user id as a string) and must be
/// enforced in SQL as <c>WHERE user_id = @owner</c> — this is the IDOR guard
/// for the new identity model. Legacy HS256 owners keep resolving through
/// login → Users.id (stored as a string).
/// </summary>
public sealed class OwnerResolutionTests : IAsyncLifetime
{
    private const string Kid = "monolith-test-key-1";

    private readonly JwksTestServer _jwks;
    private readonly MonolithAppFactory _factory;
    private readonly RsaSecurityKey _key;

    public OwnerResolutionTests()
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
    public async Task Rs256_OwnerIsSub_StringIdorIsBlocked()
    {
        var ownerSub = "9" + Guid.NewGuid().ToString("N")[..8];
        var otherSub = "9" + Guid.NewGuid().ToString("N")[..8];
        var ownerToken = TestTokens.MakeRs256(_key, Kid, sub: ownerSub, login: "owner");
        var otherToken = TestTokens.MakeRs256(_key, Kid, sub: otherSub, login: "other");

        // Upload as the owner: the row must be stored with user_id = sub.
        var uploaded = await ApiHelpers.Send(_factory, HttpMethod.Post, "/api/files/upload",
            multipart: ApiHelpers.GlbUpload($"file-{ownerSub}.glb"), jwtToken: ownerToken);
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);

        // The owner sees the file in /api/models.
        var ownerList = await ApiHelpers.Send(_factory, HttpMethod.Get, "/api/models", jwtToken: ownerToken);
        Assert.Equal(HttpStatusCode.OK, ownerList.StatusCode);
        var ownerItems = await ReadPagedItemsAsync(ownerList);
        var uploadedId = ownerItems.Single().id;
        Assert.Equal(ownerSub, await AssertStoredOwnerAsync(uploadedId, ownerSub));

        // A different sub sees an empty list...
        var otherList = await ApiHelpers.Send(_factory, HttpMethod.Get, "/api/models", jwtToken: otherToken);
        Assert.Equal(HttpStatusCode.OK, otherList.StatusCode);
        Assert.Empty(await ReadPagedItemsAsync(otherList));

        // ...cannot read the file by id (IDOR: string sub must not match)...
        var otherGet = await ApiHelpers.Send(_factory, HttpMethod.Get, $"/api/models/{uploadedId}",
            jwtToken: otherToken);
        Assert.Equal(HttpStatusCode.NotFound, otherGet.StatusCode);

        // ...and cannot delete it.
        var otherDelete = await ApiHelpers.Send(_factory, HttpMethod.Delete, $"/api/models/{uploadedId}",
            jwtToken: otherToken);
        Assert.Equal(HttpStatusCode.NotFound, otherDelete.StatusCode);

        // The owner can delete their own file.
        var ownerDelete = await ApiHelpers.Send(_factory, HttpMethod.Delete, $"/api/models/{uploadedId}",
            jwtToken: ownerToken);
        Assert.Equal(HttpStatusCode.NoContent, ownerDelete.StatusCode);
    }

    [Fact]
    public async Task LegacyHs256_OwnerResolvesThroughLogin()
    {
        var login = ApiHelpers.UniqueLogin();
        var password = "Str0ng-Passw0rd!";

        var registered = await ApiHelpers.Send(_factory, HttpMethod.Post, "/api/auth/register",
            new { fullName = "Legacy User", birthDate = new DateOnly(1985, 5, 4), login, password });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        var loginResponse = await ApiHelpers.Send(_factory, HttpMethod.Post, "/api/auth/login",
            new { login, password });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var token = LegacyTokenTests.ExtractCookieValue(
            loginResponse.Headers.GetValues("Set-Cookie").First(), "jwt");

        // Upload through the legacy HS256 cookie: the owner is the numeric
        // Users.id, stored as a string (the "5" → '5' shape).
        var uploaded = await ApiHelpers.Send(_factory, HttpMethod.Post, "/api/files/upload",
            multipart: ApiHelpers.GlbUpload($"legacy-{login}.glb"), jwtToken: token);
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);

        var list = await ApiHelpers.Send(_factory, HttpMethod.Get, "/api/models", jwtToken: token);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var items = await ReadPagedItemsAsync(list);
        var uploadedId = items.Single().id;

        // The stored owner is a decimal string (the Users.id), not a GUID.
        var storedOwner = await AssertStoredOwnerAsync(uploadedId, null);
        Assert.Matches("^[0-9]+$", storedOwner);
    }

    /// <summary>
    /// Reads the stored user_id for the file id directly from the database.
    /// Returns the stored value; asserts it equals <paramref name="expected"/>
    /// when one is given.
    /// </summary>
    private async Task<string> AssertStoredOwnerAsync(string fileId, string? expected)
    {
        var owner = await DbQuery.StoredOwnerAsync(fileId);
        if (expected is not null)
        {
            Assert.Equal(expected, owner);
        }

        return owner!;
    }

    private static async Task<List<Item>> ReadPagedItemsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        var itemsElement = doc.RootElement.TryGetProperty("items", out var items)
            ? items
            : throw new InvalidOperationException($"No 'items' in: {body}");

        var result = new List<Item>();
        foreach (var item in itemsElement.EnumerateArray())
        {
            result.Add(new Item(item.GetProperty("id").GetString()!));
        }

        return result;
    }

    private sealed record Item(string id);
}
