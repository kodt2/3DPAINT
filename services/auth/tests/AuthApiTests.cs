using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Auth.Tests;

/// <summary>
/// Integration tests for the Auth service (step 4.1, ADR 0018). They run the
/// real app in-process (<c>WebApplicationFactory</c>) against a real PostgreSQL
/// (the compose-stand database) and exercise the public HTTP contract:
/// /health, JWKS, register, login, /api/me, logout and the auth cookies.
/// </summary>
[Collection(nameof(AuthAppCollection))]
public sealed class AuthApiTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly AuthAppFactory _factory;

    public AuthApiTests(AuthAppFactory factory)
    {
        _factory = factory;
    }

    // --- helpers -----------------------------------------------------------

    private sealed record UserDto(int id, string? login, string? fullName, DateOnly? birthDate);

    private static string StrongPassword => "Str0ng-Passw0rd!";

    private static string UniqueLogin() => "user" + Guid.NewGuid().ToString("N")[..16];

    private static async Task<HttpResponseMessage> Send(
        AuthAppFactory factory,
        HttpMethod method,
        string url,
        object? body = null,
        string? cookie = null)
    {
        // The in-memory handler is used directly (no HttpClientHandler), so no
        // cookie container is involved: the Set-Cookie headers stay in the
        // response. The suite asserts the exact cookie shape (name, Path,
        // HttpOnly, Secure, SameSite) and passes cookies explicitly.
        using var client = new HttpClient(factory.Server.CreateHandler())
        {
            BaseAddress = factory.Server.BaseAddress,
        };

        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json");
        }

        if (cookie is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        return await client.SendAsync(request);
    }

    private static string? GetSetCookieHeader(HttpResponseMessage response, string name)
    {
        // GetValues(...) throws when the header is absent; TryGetValues is safe.
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return null;
        }

        foreach (var header in values)
        {
            if (header.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                return header;
            }
        }

        return null;
    }

    private static string? GetSetCookieValue(HttpResponseMessage response, string name)
    {
        var header = GetSetCookieHeader(response, name);
        return header is null ? null : header.Split(';', 2)[0][(name.Length + 1)..];
    }

    private static async Task<UserDto> ReadUser(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<UserDto>(json, JsonOpts)!;
    }

    // --- health ------------------------------------------------------------

    [Fact]
    public async Task HealthLive_Returns200()
    {
        var response = await Send(_factory, HttpMethod.Get, "/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthReady_Returns200_WhenDatabaseIsReachable()
    {
        var response = await Send(_factory, HttpMethod.Get, "/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // --- JWKS --------------------------------------------------------------

    [Fact]
    public async Task Jwks_ReturnsRsa256KeyWithKid_N_And_E()
    {
        var response = await Send(_factory, HttpMethod.Get, "/.well-known/jwks.json");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"JWKS returned {(int)response.StatusCode}, body: {body}");

        using var doc = JsonDocument.Parse(body);
        var keys = doc.RootElement.GetProperty("keys");
        Assert.True(keys.GetArrayLength() >= 1, "expected at least one key in the JWKS");

        var first = keys[0];
        Assert.Equal("RSA", first.GetProperty("kty").GetString());
        Assert.Equal("RS256", first.GetProperty("alg").GetString());
        Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("kid").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("n").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("e").GetString()));
    }

    [Fact]
    public async Task Jwks_IncludesThePreviousKey_WhenConfigured()
    {
        var response = await Send(_factory, HttpMethod.Get, "/.well-known/jwks.json");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        // The test stand configures both the current and the previous key, so
        // both must be published (the rotation window, ADR 0018).
        Assert.Equal(2, doc.RootElement.GetProperty("keys").GetArrayLength());
    }

    // --- register ----------------------------------------------------------

    [Fact]
    public async Task Register_CreatesUser_SetsBothCookies_Returns201()
    {
        var login = UniqueLogin();
        var response = await Send(
            _factory,
            HttpMethod.Post,
            "/api/auth/register",
            new { login, password = StrongPassword, fullName = "Test User", birthDate = new DateOnly(1990, 1, 1) });

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"register returned {(int)response.StatusCode}, body: {body}");

        var dto = JsonSerializer.Deserialize<UserDto>(body, JsonOpts)!;
        Assert.True(dto.id > 0, "the added id must be a positive user id");
        Assert.Equal(login, dto.login);
        Assert.Equal("Test User", dto.fullName);
        Assert.Equal(new DateOnly(1990, 1, 1), dto.birthDate);

        // The RS256 access token is in the http-only "jwt" cookie (monolith shape).
        var jwtCookie = GetSetCookieHeader(response, "jwt");
        Assert.NotNull(jwtCookie);
        Assert.Contains("HttpOnly", jwtCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Secure", jwtCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SameSite=Lax", jwtCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Path=/", jwtCookie, StringComparison.OrdinalIgnoreCase);

        // The opaque refresh token is in the path-restricted "rt" cookie.
        var rtCookie = GetSetCookieHeader(response, "rt");
        Assert.NotNull(rtCookie);
        Assert.Contains("HttpOnly", rtCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Secure", rtCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SameSite=Lax", rtCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Path=/api/auth", rtCookie, StringComparison.OrdinalIgnoreCase);

        // The refresh token is opaque: not a JWT (no dots) and not the numeric id.
        var rtValue = GetSetCookieValue(response, "rt")!;
        Assert.False(string.IsNullOrWhiteSpace(rtValue));
        Assert.DoesNotContain('.', rtValue);
        Assert.NotEqual(dto.id.ToString(), rtValue);
    }

    [Fact]
    public async Task Register_DuplicateLogin_Returns409()
    {
        var login = UniqueLogin();
        var first = await Send(
            _factory, HttpMethod.Post, "/api/auth/register",
            new { login, password = StrongPassword });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await Send(
            _factory, HttpMethod.Post, "/api/auth/register",
            new { login, password = StrongPassword });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Register_PasswordTooShort_Returns400()
    {
        var response = await Send(
            _factory, HttpMethod.Post, "/api/auth/register",
            new { login = UniqueLogin(), password = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Register_CommonPassword_Returns400()
    {
        // "password123" (length > 10) is in the built-in common-passwords list.
        var response = await Send(
            _factory, HttpMethod.Post, "/api/auth/register",
            new { login = UniqueLogin(), password = "password123" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- login -------------------------------------------------------------

    [Fact]
    public async Task Login_ValidCredentials_Returns200_AndSetsCookies()
    {
        var login = UniqueLogin();
        await Send(_factory, HttpMethod.Post, "/api/auth/register",
            new { login, password = StrongPassword });

        var response = await Send(_factory, HttpMethod.Post, "/api/auth/login",
            new { login, password = StrongPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var dto = await ReadUser(response);
        Assert.Equal(login, dto.login);
        Assert.NotNull(GetSetCookieHeader(response, "jwt"));
        Assert.NotNull(GetSetCookieHeader(response, "rt"));
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401_UniformMessage()
    {
        var login = UniqueLogin();
        await Send(_factory, HttpMethod.Post, "/api/auth/register",
            new { login, password = StrongPassword });

        var response = await Send(_factory, HttpMethod.Post, "/api/auth/login",
            new { login, password = "Wr0ng-Passw0rd!99" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var detail = await ReadProblemDetail(response);
        Assert.Equal("Неверный логин или пароль", detail);
    }

    [Fact]
    public async Task Login_UnknownUser_Returns401_SameShapeAsWrongPassword()
    {
        // ADR 0018: unknown login and wrong password are indistinguishable, so
        // logins cannot be enumerated. Both return the identical 401 message.
        var response = await Send(_factory, HttpMethod.Post, "/api/auth/login",
            new { login = UniqueLogin(), password = StrongPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var detail = await ReadProblemDetail(response);
        Assert.Equal("Неверный логин или пароль", detail);
    }

    // --- /api/me -----------------------------------------------------------

    [Fact]
    public async Task Me_WithValidJwtCookie_Returns200_User()
    {
        var login = UniqueLogin();
        var register = await Send(
            _factory, HttpMethod.Post, "/api/auth/register",
            new { login, password = StrongPassword, fullName = "Me User" });
        var jwt = GetSetCookieValue(register, "jwt")!;

        var response = await Send(_factory, HttpMethod.Get, "/api/me", cookie: $"jwt={jwt}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var dto = await ReadUser(response);
        Assert.Equal(login, dto.login);
        Assert.Equal("Me User", dto.fullName);
    }

    [Fact]
    public async Task Me_WithoutCookie_Returns401()
    {
        var response = await Send(_factory, HttpMethod.Get, "/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_WithTamperedToken_Returns401()
    {
        var login = UniqueLogin();
        var register = await Send(
            _factory, HttpMethod.Post, "/api/auth/register",
            new { login, password = StrongPassword });
        var jwt = GetSetCookieValue(register, "jwt")!;

        // Tamper with the signature (last segment): flip a full 6-bit group in
        // its middle — the final character can carry fewer than 6 data bits, so
        // flipping it may decode to the very same signature. RS256 must reject.
        var tampered = TamperSignature(jwt);
        var response = await Send(_factory, HttpMethod.Get, "/api/me", cookie: $"jwt={tampered}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Returns <paramref name="jwt"/> with one full 6-bit group of the signature
    /// segment changed, so the decoded signature is always different.
    /// </summary>
    private static string TamperSignature(string jwt)
    {
        var lastDot = jwt.LastIndexOf('.');
        var sigOffset = lastDot + 1;
        var i = sigOffset + (jwt.Length - sigOffset) / 2;
        var replacement = jwt[i] == 'a' ? 'b' : 'a';
        return jwt[..i] + replacement + jwt[(i + 1)..];
    }

    // --- logout ------------------------------------------------------------

    [Fact]
    public async Task Logout_Returns204_AndDeletesBothCookies()
    {
        var register = await Send(
            _factory, HttpMethod.Post, "/api/auth/register",
            new { login = UniqueLogin(), password = StrongPassword });
        Assert.NotNull(GetSetCookieHeader(register, "jwt"));

        var response = await Send(_factory, HttpMethod.Post, "/api/auth/logout");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var jwtCookie = GetSetCookieHeader(response, "jwt");
        Assert.NotNull(jwtCookie);
        Assert.Contains("Max-Age=0", jwtCookie, StringComparison.OrdinalIgnoreCase);

        var rtCookie = GetSetCookieHeader(response, "rt");
        Assert.NotNull(rtCookie);
        Assert.Contains("Max-Age=0", rtCookie, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> ReadProblemDetail(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("detail", out var detail) ? detail.GetString() : null;
    }
}
