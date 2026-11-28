using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Auth.Entities;
using Microsoft.AspNetCore.Cryptography.KeyDerivation;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Auth.Tests;

/// <summary>
/// Password hashing guarantees (step 4.3b pre-check, ADR 0018 §12). Auth stores
/// passwords with the ASP.NET Identity hasher; the tests pin the real behavior
/// (not the documentation) of the installed .NET 10 package:
/// - two users with the same password get different stored hashes (random salt);
/// - the stored hash is Identity version 3 (marker 0x01 after base64-decode):
///   PBKDF2-HMAC-SHA512, 100,000 iterations (the package default — the app must
///   not lower it), 16-byte salt, 32-byte subkey;
/// - the plaintext password never appears in API responses or in the logs.
/// </summary>
[Collection(nameof(AuthAppCollection))]
public sealed class PasswordHashTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly AuthAppFactory _factory;

    public PasswordHashTests(AuthAppFactory factory)
    {
        _factory = factory;
    }

    // --- helpers -----------------------------------------------------------

    private static string UniqueLogin() => "pwhash_" + Guid.NewGuid().ToString("N")[..12];

    private static string PasswordWithMarker(string marker) => $"{marker}-{Guid.NewGuid():N}";

    private static async Task<HttpResponseMessage> Send(
        AuthAppFactory factory,
        HttpMethod method,
        string url,
        object? body = null,
        string? cookie = null)
    {
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

    private static string JwtCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            throw new InvalidOperationException("The response set no cookies.");
        }

        var jwt = values
            .FirstOrDefault(value => value.StartsWith("jwt=", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrEmpty(jwt));
        return jwt!.Split(';', 2)[0];
    }

    /// <summary>Reads the stored PasswordHash for a login straight from Postgres.</summary>
    private static async Task<string?> ReadPasswordHashAsync(string login)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__AuthConnection")
            ?? throw new InvalidOperationException(
                "ConnectionStrings__AuthConnection is not set (test stand).");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT \"PasswordHash\" FROM \"AspNetUsers\" WHERE \"UserName\" = @login",
            connection);
        command.Parameters.Add("@login", NpgsqlDbType.Text).Value = login;
        return (string?)await command.ExecuteScalarAsync();
    }

    // --- tests -------------------------------------------------------------

    [Fact]
    public async Task SamePassword_TwoUsers_DifferentIdentityV3Hashes()
    {
        var password = PasswordWithMarker("Pw");
        var loginA = UniqueLogin();
        var loginB = UniqueLogin();

        var registerA = await Send(_factory, HttpMethod.Post, "/api/auth/register",
            new { login = loginA, password, FullName = "Pw Hash User", birthDate = new DateOnly(1990, 1, 1) });
        Assert.Equal(HttpStatusCode.Created, registerA.StatusCode);
        var registerB = await Send(_factory, HttpMethod.Post, "/api/auth/register",
            new { login = loginB, password, FullName = "Pw Hash User", birthDate = new DateOnly(1990, 1, 1) });
        Assert.Equal(HttpStatusCode.Created, registerB.StatusCode);

        var hashA = await ReadPasswordHashAsync(loginA);
        var hashB = await ReadPasswordHashAsync(loginB);
        Assert.False(string.IsNullOrWhiteSpace(hashA));
        Assert.False(string.IsNullOrWhiteSpace(hashB));

        // Random per-user salt: the same password must not produce the same hash.
        Assert.NotEqual(hashA, hashB);

        // Identity V3 layout: 0x01 marker, then prf / iterCount / saltLength as
        // big-endian UInt32, then the salt and the subkey (61 bytes in total).
        var bytes = Convert.FromBase64String(hashA!);
        Assert.Equal(1 + 4 + 4 + 4 + 16 + 32, bytes.Length);
        Assert.Equal(0x01, bytes[0]);

        uint BigEndian(byte[] value, int offset)
            => (uint)((value[offset] << 24) | (value[offset + 1] << 16)
                      | (value[offset + 2] << 8) | value[offset + 3]);

        // The .NET 10 Identity defaults (package source, v10.0.0,
        // src/Identity/Extensions.Core/src/PasswordHasher.cs and
        // PasswordHasherOptions.cs): PBKDF2-HMAC-SHA512, 100,000 iterations,
        // 128-bit salt, 256-bit subkey.
        Assert.Equal((uint)KeyDerivationPrf.HMACSHA512, BigEndian(bytes, 1));
        Assert.Equal(2u, BigEndian(bytes, 1)); // KeyDerivationPrf.HMACSHA512
        Assert.Equal(100_000u, BigEndian(bytes, 5));
        Assert.Equal((uint)new PasswordHasherOptions().IterationCount, BigEndian(bytes, 5));
        Assert.Equal(16u, BigEndian(bytes, 9));
        Assert.Equal(32, bytes.Length - 13 - 16);

        // The stored hash must verify with the host's configured hasher.
        // UserManager is scoped, so resolve it from a scope.
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
        var user = await userManager.FindByNameAsync(loginA);
        Assert.NotNull(user);
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AuthUser>>();
        Assert.Equal(
            PasswordVerificationResult.Success,
            hasher.VerifyHashedPassword(user, hashA, password));
    }

    [Fact]
    public async Task Password_NeverAppearsInApiResponses()
    {
        var password = PasswordWithMarker("RespLeak");
        var login = UniqueLogin();

        var register = await Send(_factory, HttpMethod.Post, "/api/auth/register",
            new { login, password, FullName = "Pw Hash User", birthDate = new DateOnly(1990, 1, 1) });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        Assert.DoesNotContain(password, await register.Content.ReadAsStringAsync());

        var loginResponse = await Send(_factory, HttpMethod.Post, "/api/auth/login",
            new { login, password });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        Assert.DoesNotContain(password, await loginResponse.Content.ReadAsStringAsync());

        var me = await Send(_factory, HttpMethod.Get, "/api/me", cookie: JwtCookie(loginResponse));
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.DoesNotContain(password, await me.Content.ReadAsStringAsync());

        // A policy violation (shorter than Password.MinLength) must not echo the
        // password back either.
        var weakPassword = "weak" + Guid.NewGuid().ToString("N")[..4];
        var rejected = await Send(_factory, HttpMethod.Post, "/api/auth/register",
            new { login = UniqueLogin(), password = weakPassword, FullName = "Pw Hash User", birthDate = new DateOnly(1990, 1, 1) });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.DoesNotContain(weakPassword, await rejected.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Password_NeverAppearsInLogs()
    {
        var password = PasswordWithMarker("LogLeak");
        var login = UniqueLogin();

        var register = await Send(_factory, HttpMethod.Post, "/api/auth/register",
            new { login, password, FullName = "Pw Hash User", birthDate = new DateOnly(1990, 1, 1) });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var loginResponse = await Send(_factory, HttpMethod.Post, "/api/auth/login",
            new { login, password });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        // The sink must actually be receiving events (no vacuous pass): the
        // request-logging middleware writes one Information line per request.
        var lines = _factory.LogSink.Lines;
        Assert.NotEmpty(lines);
        Assert.Contains(
            lines, line => line.Contains("/api/auth/register", StringComparison.Ordinal));

        var leaked = lines
            .FirstOrDefault(line => line.Contains(password, StringComparison.Ordinal));
        if (leaked is not null)
        {
            var tail = string.Join(Environment.NewLine, lines.TakeLast(50));
            Assert.Fail(
                $"The plaintext password leaked into the logs: {leaked}" +
                Environment.NewLine + "--- last captured log lines ---" +
                Environment.NewLine + tail);
        }
    }
}
