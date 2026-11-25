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

// Envelope headroom (ADR 0015): the body limit applies to the whole multipart
// body (file bytes + boundary/headers), so a file exactly at the limit must
// still pass the gateway; the monolith's app-level check (file size vs the
// same limit) is authoritative.
const long bodySlack = 1L * 1024L * 1024L;
var maxRequestBody = maxUploadBytes + bodySlack;

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
    options.Limits.MaxRequestBodySize = maxRequestBody;
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

// Route scheme (ADR 0011, updated in step 3d.2). Only explicit routes exist
// (no catch-all): the Razor UI was removed from the monolith, so unknown
// paths are no longer proxied to it. The gateway returns 404 for anything
// that is not /api/**, /spa/** or /assets/** (MapFallback below). Old Razor
// page routes (/Home/**, /Account/**) are 302-redirected to /spa/ by
// LegacyRazorRedirectMiddleware before routing.
//   1. /api/**    -> monolith (JSON API, incl. /api/files/upload)
//   2. /assets/** -> frontend (hashed build assets, immutable caching)
//   3. /spa/**    -> frontend (SPA pages, prefix stripped)
var apiRoute = new RouteConfig
{
    RouteId = "api-to-monolith",
    ClusterId = "monolith",
    Order = 1,
    MaxRequestBodySize = maxRequestBody,
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
    new[] { apiRoute, assetsRoute, spaRoute },
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

// Site root -> SPA (3d.1): a relative 302 to /spa/ (no absolute URL, ADR 0016).
// Registered before any Map* (health, reverse proxy) so it runs before routing;
// a relative Location keeps the origin and any non-standard port (https/Caddy).
app.UseMiddleware<RootRedirectMiddleware>();

// Old Razor page routes (/Home/**, /Account/**) -> SPA (3d.2): the Razor UI
// was removed from the monolith, so these page paths no longer exist. A
// relative 302 to /spa/ for safe methods (GET/HEAD); other methods fall
// through to the 404 (no monolith catch-all). Registered next to
// RootRedirectMiddleware, before routing.
app.UseMiddleware<LegacyRazorRedirectMiddleware>();

// Liveness must not depend on the upstream: run no checks, always healthy while the process is up.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks(
    "/health/ready",
    new HealthCheckOptions { Predicate = registration => registration.Tags.Contains(UpstreamHealthCheck.Tag) });

app.MapReverseProxy();

// Unknown paths -> 404 from the gateway (3d.2). The monolith's catch-all was
// removed with the Razor UI, and there is deliberately no catch-all to the
// frontend either: only /api/**, /spa/** and /assets/** are proxied. The
// relative 302s for "/" and the old Razor routes are handled by the redirect
// middlewares above, before routing.
app.MapFallback(async context =>
{
    context.Response.StatusCode = StatusCodes.Status404NotFound;
    await context.Response.WriteAsJsonAsync(new
    {
        type = "https://httpwg.org/specs/rfc9457.html#section.problem",
        title = "Not Found",
        status = 404,
    });
});

await app.RunAsync();

return 0;

public partial class Program;
