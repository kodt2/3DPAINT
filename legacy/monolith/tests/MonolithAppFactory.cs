using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace LoginAndReg.Tests;

/// <summary>
/// Runs the real monolith in-process (<c>WebApplicationFactory</c>) against
/// the compose-stand PostgreSQL and SeaweedFS. Every test class gets its own
/// factory (hence its own <c>JwksKeyProvider</c> cache): the JWKS URL is a
/// constructor parameter so a class can point the app at a live JWKS test
/// server, at a dead port, or at a document with a different key set.
///
/// All configuration is overridden in-memory (highest priority) — the app's
/// appsettings.json and the stand environment variables never leak into the
/// tests.
/// </summary>
public sealed class MonolithAppFactory : WebApplicationFactory<Program>
{
    public const string JwtKey = "test-only-monolith-hs256-secret-key-0123456789abcdef";
    public const string JwtIssuer = "3dpaint.monolith.test";
    public const string JwtAudience = "3dpaint.web.test";

    public const string AuthIssuer = "3dpaint.auth";
    public const string AuthAudience = "3dpaint";

    public const string DbConnection =
        "Host=monolith-test-db;Port=5432;Database=monolith_test;Username=app;Password=app";
    public const string StorageEndpoint = "monolith-test-storage:9000";
    public const string Bucket = "monolith-test";

    public string JwksUrl { get; }

    public MonolithAppFactory(string jwksUrl)
    {
        JwksUrl = jwksUrl;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            // ':' is the configuration path separator in in-memory sources
            // ('__' is only the environment-variable escape; with it the
            // entries stay flat top-level keys and never merge into the
            // sections). Highest-priority source: overrides appsettings and
            // the stand environment.
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = DbConnection,
                ["Jwt:Key"] = JwtKey,
                ["Jwt:Issuer"] = JwtIssuer,
                ["Jwt:Audience"] = JwtAudience,
                ["Jwt:ExpiryMinutes"] = "60",
                // Exact expiry semantics in the tests (no 30 s default skew).
                ["Jwt:ClockSkewSeconds"] = "0",
                ["AuthJwks:Url"] = JwksUrl,
                ["AuthJwks:Issuer"] = AuthIssuer,
                ["AuthJwks:Audience"] = AuthAudience,
                ["AuthJwks:RequireHttps"] = "false",
                ["AuthJwks:RefreshThrottleSeconds"] = "30",
                ["Minio:Endpoint"] = StorageEndpoint,
                ["Minio:AccessKey"] = "app",
                ["Minio:SecretKey"] = "app",
                ["Minio:BucketName"] = Bucket,
                ["Minio:Region"] = "us-east-1",
                ["Minio:UseSsl"] = "false",
                ["MaxUploadBytes"] = "10485760",
                ["AllowedUploadExtensions"] = ".glb",
            });
        });
    }
}
