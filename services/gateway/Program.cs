using System.Net.Http;
using Gateway.Health;
using Gateway.Middleware;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Serilog;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

var builder = WebApplication.CreateBuilder(args);

var upstreamAddress = builder.Configuration["Upstream:Monolith"]
    ?? throw new InvalidOperationException("Upstream:Monolith is not configured.");
var frontendAddress = builder.Configuration["Upstream:Frontend"]
    ?? throw new InvalidOperationException("Upstream:Frontend is not configured.");
var maxUploadBytes = builder.Configuration.GetValue("MaxUploadBytes", 100 * 1024 * 1024);

// Shared request transforms for proxied routes.
static List<IReadOnlyDictionary<string, string>> StandardTransforms()
{
    // Keep the original host for URL generation and form/origin checks;
    // add X-Forwarded-For/Host/Proto so the upstream sees the client chain.
    return new List<IReadOnlyDictionary<string, string>>
    {
        new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
        new Dictionary<string, string> { ["X-Forwarded"] = "Set" },
    };
}

// The SPA is served under /spa/**; nginx inside the frontend container expects
// paths without the prefix (SPA fallback to index.html).
static List<IReadOnlyDictionary<string, string>> SpaTransforms()
{
    var transforms = StandardTransforms();
    transforms.Add(new Dictionary<string, string> { ["PathRemovePrefix"] = "/spa" });
    return transforms;
}

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console(
        outputTemplate:
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
    .CreateLogger();
builder.Host.UseSerilog();

// The gateway must not cut uploads up to the configured limit (default 100 MiB).
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxUploadBytes;
    // Never abort a slow but active upload: 10 bytes per second is enough for liveness.
    options.Limits.MinRequestBodyDataRate = new MinDataRate(10, TimeSpan.FromSeconds(5));
});

builder.Services.AddSingleton<UpstreamHealthCheck>(sp =>
    new UpstreamHealthCheck(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(UpstreamHealthCheck.ClientName),
        upstreamAddress));
builder.Services
    .AddHttpClient(UpstreamHealthCheck.ClientName)
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(2));
builder.Services.AddHealthChecks()
    .AddCheck<UpstreamHealthCheck>(UpstreamHealthCheck.Name, tags: new[] { UpstreamHealthCheck.Tag });

// Route scheme (ADR 0011). Explicit routes win over the catch-all by Order:
//   1. /api/**    -> monolith (JSON API, incl. /api/files/upload)
//   2. /assets/** -> frontend (hashed build assets, immutable caching)
//   3. /spa/**    -> frontend (SPA pages, prefix stripped)
//   4. /**        -> monolith (Razor UI, static files)
var apiRoute = new RouteConfig
{
    RouteId = "api-to-monolith",
    ClusterId = "monolith",
    Order = 1,
    MaxRequestBodySize = maxUploadBytes,
    Match = new RouteMatch { Path = "/api/{**catch-all}" },
    Transforms = StandardTransforms(),
};
var assetsRoute = new RouteConfig
{
    RouteId = "assets-to-frontend",
    ClusterId = "frontend",
    Order = 2,
    Match = new RouteMatch { Path = "/assets/{**catch-all}" },
    Transforms = StandardTransforms(),
};
var spaRoute = new RouteConfig
{
    RouteId = "spa-to-frontend",
    ClusterId = "frontend",
    Order = 3,
    Match = new RouteMatch { Path = "/spa/{**catch-all}" },
    Transforms = SpaTransforms(),
};
var monolithRoute = new RouteConfig
{
    RouteId = "monolith",
    ClusterId = "monolith",
    Order = 4,
    MaxRequestBodySize = maxUploadBytes,
    Match = new RouteMatch { Path = "{**catch-all}" },
    Transforms = StandardTransforms(),
};
var monolithCluster = new ClusterConfig
{
    ClusterId = "monolith",
    // Long uploads must not be aborted by the 100 s default activity timeout.
    // The monolith listens on plain HTTP/1.1, so pin the protocol instead of the
    // YARP default (HTTP/2 prior knowledge over h2c).
    HttpRequest = new ForwarderRequestConfig
    {
        ActivityTimeout = TimeSpan.FromMinutes(10),
        Version = new Version(1, 1),
        VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
    },
    Destinations = new Dictionary<string, DestinationConfig>
    {
        ["primary"] = new DestinationConfig { Address = upstreamAddress },
    },
};
var frontendCluster = new ClusterConfig
{
    ClusterId = "frontend",
    // nginx inside the frontend container speaks plain HTTP/1.1.
    HttpRequest = new ForwarderRequestConfig
    {
        Version = new Version(1, 1),
        VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
    },
    Destinations = new Dictionary<string, DestinationConfig>
    {
        ["primary"] = new DestinationConfig { Address = frontendAddress },
    },
};
builder.Services.AddReverseProxy().LoadFromMemory(
    new[] { apiRoute, assetsRoute, spaRoute, monolithRoute },
    new[] { monolithCluster, frontendCluster });

// Per-client key for the auth rate limit (ADR 0011). By default no proxy is
// trusted: the client key is the socket peer and X-Forwarded-For is ignored.
// Set KNOWN_PROXIES / KNOWN_NETWORKS when the gateway sits behind a TLS proxy.
builder.Services.AddSingleton<IClientKeyResolver>(TrustedProxyClientKeyResolver.FromConfiguration(builder.Configuration));

// In-memory sliding window for the auth rate limit (ADR 0011). Reset on restart
// is acceptable: it damps brute force, it is not an accounting ledger.
var rateLimitWindowSeconds = AuthRateLimitMiddleware.ReadPositiveInt(
    builder.Configuration,
    AuthRateLimitMiddleware.WindowSecondsSetting,
    AuthRateLimitMiddleware.WindowSecondsEnv,
    AuthRateLimitMiddleware.DefaultWindowSeconds);
builder.Services.AddSingleton(new SlidingWindowCounter(TimeSpan.FromSeconds(rateLimitWindowSeconds)));

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();

app.UseSerilogRequestLogging();

// Auth rate limit (ADR 0011): before everything else that touches the monolith,
// and before the origin check — a flood of bad-origin requests is still a flood.
app.UseMiddleware<AuthRateLimitMiddleware>();

// In dev the gateway is the edge; trust forwarded headers from the browser/proxy chain.
app.UseForwardedHeaders();

// CSRF protection for state-changing /api/** requests: the Origin (or Referer)
// must match the gateway's public origin (PUBLIC_ORIGIN, ADR 0011). Must run
// before the reverse proxy so rejected requests never reach the monolith.
app.UseMiddleware<OriginCheckMiddleware>();

// Liveness must not depend on the upstream: run no checks, always healthy while the process is up.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks(
    "/health/ready",
    new HealthCheckOptions { Predicate = registration => registration.Tags.Contains(UpstreamHealthCheck.Tag) });

app.MapReverseProxy();

await app.RunAsync();

return 0;

public partial class Program;
