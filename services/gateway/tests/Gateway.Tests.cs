using System.Collections.Specialized;
using System.Net;
using Gateway.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Gateway.Tests;

public sealed class GatewayTests : IDisposable
{
    private readonly StubUpstream _upstream = new();

    [Fact]
    public async Task Proxy_forwards_request_to_upstream_and_generates_correlation_id()
    {
        using var factory = CreateFactory(_upstream.Url);
        var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Host = "shop.example.com";
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var generated = ReadCorrelationId(response);
        Assert.False(string.IsNullOrWhiteSpace(generated), "X-Correlation-Id must be generated");

        var upstreamHeaders = _upstream.LastRequestHeaders;
        Assert.NotNull(upstreamHeaders);
        Assert.Equal(generated, upstreamHeaders[CorrelationIdMiddleware.HeaderName]);
        // Original host must reach the upstream unchanged.
        Assert.Equal("shop.example.com", upstreamHeaders["Host"]);
    }

    [Fact]
    public async Task Proxy_adds_x_forwarded_headers_for_upstream()
    {
        using var factory = CreateFactory(_upstream.Url);
        var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Host = "shop.example.com";
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var upstreamHeaders = _upstream.LastRequestHeaders;
        Assert.NotNull(upstreamHeaders);
        // The gateway is the edge in dev: it must add the forwarded chain so the
        // upstream can generate correct external URLs and see the client address.
        // (X-Forwarded-For depends on the client IP, which TestServer does not set.)
        Assert.Equal("shop.example.com", upstreamHeaders["X-Forwarded-Host"]);
        Assert.Equal("http", upstreamHeaders["X-Forwarded-Proto"]);
    }

    [Fact]
    public async Task Proxy_passes_incoming_correlation_id_through()
    {
        using var factory = CreateFactory(_upstream.Url);
        var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, "abc-123");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("abc-123", ReadCorrelationId(response));
        Assert.Equal("abc-123", _upstream.LastRequestHeaders?[CorrelationIdMiddleware.HeaderName]);
    }

    [Fact]
    public async Task Health_live_returns_200()
    {
        using var factory = CreateFactory(_upstream.Url);
        var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_ready_returns_200_when_upstream_is_available()
    {
        using var factory = CreateFactory(_upstream.Url);
        var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_ready_returns_503_when_upstream_is_unavailable()
    {
        // Port 1 on loopback: nothing listens there, connection is refused.
        using var factory = CreateFactory("http://127.0.0.1:1");
        var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private static string ReadCorrelationId(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues(CorrelationIdMiddleware.HeaderName, out var values),
            "Response must carry X-Correlation-Id");
        return string.Join(",", values!);
    }

    private static WebApplicationFactory<Program> CreateFactory(string upstreamAddress)
        => new TestAppFactory()
            .WithWebHostBuilder(builder => builder.UseSetting("Upstream:Monolith", upstreamAddress));

    private sealed class TestAppFactory : WebApplicationFactory<Program>
    {
    }

    public void Dispose() => _upstream.Dispose();
}

/// <summary>
/// Minimal Kestrel-based HTTP stub that records the headers of the last request
/// and answers 200. Kestrel (like the real monolith) accepts any Host header.
/// </summary>
internal sealed class StubUpstream : IDisposable
{
    private readonly WebApplication _app;

    public StubUpstream()
    {
        var port = GetFreePort();
        Url = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(Url);
        _app = builder.Build();
        _app.Run(context =>
        {
            var collection = new NameValueCollection();
            foreach (var (key, value) in context.Request.Headers)
            {
                collection[key] = value.ToString();
            }

            LastRequestHeaders = collection;
            return context.Response.WriteAsync("stub-ok");
        });
        _app.StartAsync().GetAwaiter().GetResult();
    }

    public string Url { get; }

    public NameValueCollection? LastRequestHeaders { get; private set; }

    private static int GetFreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _app.StopAsync(cts.Token).GetAwaiter().GetResult();
        _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
