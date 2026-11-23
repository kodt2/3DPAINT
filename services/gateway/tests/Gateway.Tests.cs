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
    private readonly StubUpstream _upstream = new("monolith");

    [Fact]
    public async Task Proxy_forwards_request_to_upstream_and_generates_correlation_id()
    {
        using var factory = CreateFactory(_upstream.Url, _upstream.Url);
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
        using var factory = CreateFactory(_upstream.Url, _upstream.Url);
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
        using var factory = CreateFactory(_upstream.Url, _upstream.Url);
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
        using var factory = CreateFactory(_upstream.Url, _upstream.Url);
        var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_ready_returns_200_when_upstream_is_available()
    {
        using var factory = CreateFactory(_upstream.Url, _upstream.Url);
        var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_ready_returns_503_when_upstream_is_unavailable()
    {
        // Port 1 on loopback: nothing listens there, connection is refused.
        using var factory = CreateFactory("http://127.0.0.1:1", _upstream.Url);
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

    [Fact]
    public async Task Route_priority_api_goes_to_monolith()
    {
        using var frontend = new StubUpstream("frontend");
        using var factory = CreateFactory(_upstream.Url, frontend.Url);
        var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/files");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("monolith-ok", await response.Content.ReadAsStringAsync());
        Assert.Equal("/api/files", _upstream.LastRequestPath);
        Assert.Null(frontend.LastRequestPath);
    }

    [Fact]
    public async Task Route_priority_assets_goes_to_frontend_with_path_unchanged()
    {
        using var frontend = new StubUpstream("frontend");
        using var factory = CreateFactory(_upstream.Url, frontend.Url);
        var client = factory.CreateClient();

        using var response = await client.GetAsync("/assets/index-abc123.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("frontend-ok", await response.Content.ReadAsStringAsync());
        Assert.Equal("/assets/index-abc123.js", frontend.LastRequestPath);
        Assert.Null(_upstream.LastRequestPath);
    }

    [Fact]
    public async Task Route_priority_spa_goes_to_frontend_with_prefix_stripped()
    {
        using var frontend = new StubUpstream("frontend");
        using var factory = CreateFactory(_upstream.Url, frontend.Url);
        var client = factory.CreateClient();

        using var response = await client.GetAsync("/spa/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("frontend-ok", await response.Content.ReadAsStringAsync());
        Assert.Equal("/models", frontend.LastRequestPath);
        Assert.Null(_upstream.LastRequestPath);
    }

    [Fact]
    public async Task Route_priority_catch_all_goes_to_monolith()
    {
        using var frontend = new StubUpstream("frontend");
        using var factory = CreateFactory(_upstream.Url, frontend.Url);
        var client = factory.CreateClient();

        using var root = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        Assert.Equal("monolith-ok", await root.Content.ReadAsStringAsync());
        Assert.Equal("/", _upstream.LastRequestPath);

        using var account = await client.GetAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.OK, account.StatusCode);
        Assert.Equal("monolith-ok", await account.Content.ReadAsStringAsync());
        Assert.Equal("/Account/Login", _upstream.LastRequestPath);
        Assert.Null(frontend.LastRequestPath);
    }

    private static WebApplicationFactory<Program> CreateFactory(string upstreamAddress, string frontendAddress)
        => new TestAppFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Upstream:Monolith", upstreamAddress);
                builder.UseSetting("Upstream:Frontend", frontendAddress);
            });

    public void Dispose() => _upstream.Dispose();
}

internal sealed class TestAppFactory : WebApplicationFactory<Program>
{
}

public sealed class OriginCheckTests : IDisposable
{
    private const string PublicOrigin = "http://allowed.example";

    private readonly StubUpstream _upstream = new("monolith");

    private static WebApplicationFactory<Program> CreateFactory(string upstreamAddress, params (string Key, string Value)[] settings)
    {
        var factory = new TestAppFactory();
        return factory.WithWebHostBuilder(builder =>
        {
            // The default public origin derives from WEB_PORT; pin it so tests
            // do not depend on the host environment.
            builder.UseSetting("WEB_PORT", "8080");
            builder.UseSetting("Upstream:Monolith", upstreamAddress);
            builder.UseSetting("Upstream:Frontend", "http://127.0.0.1:1");
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });
    }

    private static HttpRequestMessage Mutation(HttpMethod method, string path)
    {
        // StringContent sets Content-Type itself; the Origin/Referer headers are
        // added per test.
        var request = new HttpRequestMessage(method, path);
        request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        return request;
    }

    [Fact]
    public async Task Post_api_with_matching_origin_reaches_upstream()
    {
        using var factory = CreateFactory(_upstream.Url, ("PublicOrigin", PublicOrigin));
        var client = factory.CreateClient();

        var request = Mutation(HttpMethod.Post, "/api/models");
        request.Headers.Add("Origin", PublicOrigin);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/api/models", _upstream.LastRequestPath);
    }

    [Fact]
    public async Task PublicOrigin_env_var_spelling_is_read()
    {
        // Regression: the environment provider registers env vars under their
        // exact name ("PUBLIC_ORIGIN"), not the PascalCase appsettings key.
        // The middleware must accept the env spelling (docker compose sets it
        // as an env var, see infra/compose.dev.yml).
        using var factory = CreateFactory(_upstream.Url, ("PUBLIC_ORIGIN", PublicOrigin));
        var client = factory.CreateClient();

        var request = Mutation(HttpMethod.Post, "/api/models");
        request.Headers.Add("Origin", PublicOrigin);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Post_api_with_foreign_origin_returns_403_problem_details()
    {
        using var factory = CreateFactory(_upstream.Url, ("PublicOrigin", PublicOrigin));
        var client = factory.CreateClient();

        var request = Mutation(HttpMethod.Post, "/api/models");
        request.Headers.Add("Origin", "http://evil.example");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":403", body);
        Assert.Contains("\"title\":\"Forbidden\"", body);
        // The request must not have reached the upstream.
        Assert.Null(_upstream.LastRequestPath);
    }

    [Fact]
    public async Task Post_api_without_origin_or_referer_returns_403()
    {
        using var factory = CreateFactory(_upstream.Url, ("PublicOrigin", PublicOrigin));
        var client = factory.CreateClient();

        using var response = await client.SendAsync(Mutation(HttpMethod.Post, "/api/models"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(_upstream.LastRequestPath);
    }

    [Fact]
    public async Task Post_api_without_origin_but_matching_referer_reaches_upstream()
    {
        using var factory = CreateFactory(_upstream.Url, ("PublicOrigin", PublicOrigin));
        var client = factory.CreateClient();

        var request = Mutation(HttpMethod.Post, "/api/models");
        request.Headers.Add("Referer", $"{PublicOrigin}/spa/login");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/api/models", _upstream.LastRequestPath);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task Read_methods_without_origin_reach_upstream(string method)
    {
        using var factory = CreateFactory(_upstream.Url, ("PublicOrigin", PublicOrigin));
        var client = factory.CreateClient();

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/api/files"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/api/files", _upstream.LastRequestPath);
    }

    [Fact]
    public async Task Delete_api_with_foreign_origin_returns_403()
    {
        using var factory = CreateFactory(_upstream.Url, ("PublicOrigin", PublicOrigin));
        var client = factory.CreateClient();

        var request = Mutation(HttpMethod.Delete, "/api/models/123");
        request.Headers.Add("Origin", "http://evil.example");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(_upstream.LastRequestPath);
    }

    [Fact]
    public async Task Non_api_mutation_without_origin_reaches_upstream()
    {
        using var factory = CreateFactory(_upstream.Url, ("PublicOrigin", PublicOrigin));
        var client = factory.CreateClient();

        // The Razor pages stay on their own routes and keep form posts working.
        using var response = await client.SendAsync(Mutation(HttpMethod.Post, "/Account/Login"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/Account/Login", _upstream.LastRequestPath);
    }

    [Fact]
    public async Task Default_public_origin_uses_web_port()
    {
        // No PublicOrigin setting: the default is http://localhost:{WEB_PORT}.
        using var factory = CreateFactory(_upstream.Url);
        var client = factory.CreateClient();

        var allowed = Mutation(HttpMethod.Post, "/api/models");
        allowed.Headers.Add("Origin", "http://localhost:8080");
        using var allowedResponse = await client.SendAsync(allowed);
        Assert.Equal(HttpStatusCode.OK, allowedResponse.StatusCode);

        var foreign = Mutation(HttpMethod.Post, "/api/models");
        foreign.Headers.Add("Origin", "http://localhost:9999");
        using var foreignResponse = await client.SendAsync(foreign);
        Assert.Equal(HttpStatusCode.Forbidden, foreignResponse.StatusCode);
    }

    [Fact]
    public async Task Referer_evil_domain_resembling_allowed_origin_returns_403()
    {
        // "localhost:8080.evil.com" is a lookalike, not a prefix match:
        // origins are compared as parsed URLs (host must match exactly).
        // TryAddWithoutValidation: the Referer value is not a valid URI
        // (a port must be numeric), so the typed Referer header parser
        // rejects it — but the middleware must handle such a header value
        // when a proxy or client passes it through.
        using var factory = CreateFactory(_upstream.Url, ("PublicOrigin", "http://localhost:8080"));
        var client = factory.CreateClient();

        var request = Mutation(HttpMethod.Post, "/api/models");
        request.Headers.TryAddWithoutValidation("Referer", "http://localhost:8080.evil.com/spa/login");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(_upstream.LastRequestPath);

        // A valid-URI lookalike with the allowed origin in the path:
        // only the host part of the Referer is the origin.
        var pathLookalike = Mutation(HttpMethod.Post, "/api/models");
        pathLookalike.Headers.Add("Referer", "http://evil.com/localhost:8080/spa/login");
        using var pathResponse = await client.SendAsync(pathLookalike);

        Assert.Equal(HttpStatusCode.Forbidden, pathResponse.StatusCode);
        Assert.Null(_upstream.LastRequestPath);
    }

    [Fact]
    public async Task Origin_literal_null_returns_403()
    {
        // Browsers send the literal string "null" as Origin for opaque
        // origins (file://, redirected cross-origin requests). It is not an
        // absolute URL and must be rejected.
        using var factory = CreateFactory(_upstream.Url, ("PublicOrigin", "http://allowed.example"));
        var client = factory.CreateClient();

        var request = Mutation(HttpMethod.Post, "/api/models");
        request.Headers.Add("Origin", "null");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(_upstream.LastRequestPath);
    }

    [Fact]
    public async Task Referer_own_origin_with_path_and_query_reaches_upstream()
    {
        // The Referer carries the full URL; only its origin part matters.
        using var factory = CreateFactory(_upstream.Url, ("PublicOrigin", "http://allowed.example"));
        var client = factory.CreateClient();

        var request = Mutation(HttpMethod.Post, "/api/models");
        request.Headers.Add("Referer", "http://allowed.example/spa/login?next=/library");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/api/models", _upstream.LastRequestPath);
    }

    [Fact]
    public async Task Nonstandard_port_matches_exactly_but_not_as_prefix()
    {
        using var factory = CreateFactory(_upstream.Url, ("PublicOrigin", "http://localhost:9443"));
        var client = factory.CreateClient();

        // Neither the truncated port nor the port with an extra digit is the
        // allowed origin (a string-prefix comparison would let these through).
        foreach (var lookalike in new[] { "http://localhost:944", "http://localhost:94431" })
        {
            var request = Mutation(HttpMethod.Post, "/api/models");
            request.Headers.Add("Origin", lookalike);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Null(_upstream.LastRequestPath);
        }

        var exact = Mutation(HttpMethod.Post, "/api/models");
        exact.Headers.Add("Origin", "http://localhost:9443");
        using var exactResponse = await client.SendAsync(exact);
        Assert.Equal(HttpStatusCode.OK, exactResponse.StatusCode);
        Assert.Equal("/api/models", _upstream.LastRequestPath);
    }

    [Theory]
    [InlineData("https://example.com", "https://example.com:443")]
    [InlineData("https://example.com:443", "https://example.com")]
    [InlineData("http://example.com", "http://example.com:80")]
    public async Task Default_port_variants_are_the_same_origin(string configuredOrigin, string requestOrigin)
    {
        // 443/80 are default ports: the scheme must not change, but the port
        // may be explicit or omitted.
        using var factory = CreateFactory(_upstream.Url, ("PublicOrigin", configuredOrigin));
        var client = factory.CreateClient();

        var request = Mutation(HttpMethod.Post, "/api/models");
        request.Headers.Add("Origin", requestOrigin);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/api/models", _upstream.LastRequestPath);
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
    private readonly string _marker;

    public StubUpstream(string marker)
    {
        _marker = marker;
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
            LastRequestPath = context.Request.Path.Value;
            return context.Response.WriteAsync($"{_marker}-ok");
        });
        _app.StartAsync().GetAwaiter().GetResult();
    }

    public string Url { get; }

    public NameValueCollection? LastRequestHeaders { get; private set; }

    public string? LastRequestPath { get; private set; }

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
