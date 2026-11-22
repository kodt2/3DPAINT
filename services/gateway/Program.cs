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
var maxUploadBytes = builder.Configuration.GetValue("MaxUploadBytes", 100 * 1024 * 1024);

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

var monolithRoute = new RouteConfig
{
    RouteId = "monolith",
    ClusterId = "monolith",
    Order = 1,
    MaxRequestBodySize = maxUploadBytes,
    Match = new RouteMatch { Path = "{**catch-all}" },
    // Keep the original host for URL generation and form/origin checks;
    // add X-Forwarded-For/Host/Proto so the upstream sees the client chain.
    Transforms = new List<IReadOnlyDictionary<string, string>>
    {
        new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
        new Dictionary<string, string> { ["X-Forwarded"] = "Set" },
    },
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
builder.Services.AddReverseProxy().LoadFromMemory(new[] { monolithRoute }, new[] { monolithCluster });

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();

app.UseSerilogRequestLogging();

// In dev the gateway is the edge; trust forwarded headers from the browser/proxy chain.
app.UseForwardedHeaders();

// Liveness must not depend on the upstream: run no checks, always healthy while the process is up.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks(
    "/health/ready",
    new HealthCheckOptions { Predicate = registration => registration.Tags.Contains(UpstreamHealthCheck.Tag) });

app.MapReverseProxy();

await app.RunAsync();

return 0;

public partial class Program;
