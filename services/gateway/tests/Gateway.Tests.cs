using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Net;
using Gateway.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
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

        // A monolith path: only /api/** is proxied to the monolith (3d.2
        // removed the catch-all), so proxy behaviour is exercised on an /api
        // path.
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/correlation-check");
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

        // Monolith path (only /api/** is proxied to the monolith, 3d.2).
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/forwarded-check");
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

        // Monolith path (only /api/** is proxied to the monolith, 3d.2).
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/correlation-check");
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
    public async Task Health_ready_returns_200_when_api_health_returns_200()
    {
        // The default stub returns 200 for /api/health, so the gateway is ready.
        using var factory = CreateFactory(_upstream.Url, _upstream.Url);
        var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_ready_returns_503_when_api_health_returns_404()
    {
        // The 3d.2 regression: the monolith /api surface returning 404 must NOT
        // be considered ready. The previous status-agnostic probe (any < 500)
        // would have marked it healthy; probing /api/health for exactly 200
        // catches it.
        using var badUpstream = new StubUpstream("monolith");
        badUpstream.SetStatus("/api/health", 404);
        using var factory = CreateFactory(badUpstream.Url, _upstream.Url);
        var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Health_ready_returns_503_when_api_health_returns_500()
    {
        using var badUpstream = new StubUpstream("monolith");
        badUpstream.SetStatus("/api/health", 500);
        using var factory = CreateFactory(badUpstream.Url, _upstream.Url);
        var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
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
    public async Task Unknown_paths_return_404_and_reach_no_upstream()
    {
        using var frontend = new StubUpstream("frontend");
        using var factory = CreateFactory(_upstream.Url, frontend.Url);
        var client = factory.CreateClient();

        // 3d.2 removed the monolith catch-all: a path that is not /api/**,
        // /spa/** or /assets/** is a 404 from the gateway and never reaches
        // either upstream.
        using var response = await client.GetAsync("/legacy");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(_upstream.LastRequestPath);
        Assert.Null(frontend.LastRequestPath);
    }

    [Fact]
    public async Task Legacy_razor_routes_redirect_to_spa_with_relative_location()
    {
        using var factory = CreateFactory(_upstream.Url, _upstream.Url);
        using var client = CreateNoRedirectClient(factory);

        // The Razor UI was removed from the monolith (3d.2): its page routes
        // /Home/** and /Account/** are 302-redirected to the SPA root with a
        // relative Location (no absolute URL, ADR 0016). Lookalikes such as
        // "/Homepage" are not matched.
        foreach (var path in new[] { "/Home/Index", "/Home", "/Account/Login", "/Account/Register", "/Account" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            Assert.Equal(LegacyRazorRedirectMiddleware.SpaRootPath, response.Headers.Location?.ToString());
        }

        // A lookalike that is not a legacy page route is not redirected: it is
        // an unknown path and a 404.
        using var lookalike = await client.GetAsync("/Homepage");
        Assert.Equal(HttpStatusCode.NotFound, lookalike.StatusCode);

        // Served by the gateway, not proxied to the monolith.
        Assert.Null(_upstream.LastRequestPath);
    }

    [Fact]
    public async Task Legacy_razor_post_is_not_redirected_and_returns_404()
    {
        using var factory = CreateFactory(_upstream.Url, _upstream.Url);
        var client = factory.CreateClient();

        // Only safe methods (GET/HEAD) redirect; a POST to a removed Razor
        // route is not proxied (no catch-all) and is a 404.
        var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login");
        request.Content = new StringContent("", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(_upstream.LastRequestPath);
    }

    [Fact]
    public async Task Root_get_redirects_to_spa_with_relative_location()
    {
        using var factory = CreateFactory(_upstream.Url, _upstream.Url);
        using var client = CreateNoRedirectClient(factory);

        using var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        // Relative Location (no scheme/host): the browser and any upstream TLS
        // proxy (Caddy) resolve it against the request origin, so a
        // non-standard HTTPS port is preserved without the gateway knowing the
        // public address (ADR 0016, 3d.1).
        Assert.Equal(RootRedirectMiddleware.SpaRootPath, response.Headers.Location?.ToString());
        // The redirect is served by the gateway, not proxied to the monolith.
        Assert.Null(_upstream.LastRequestPath);
    }

    [Fact]
    public async Task Root_head_redirects_to_spa_with_relative_location()
    {
        using var factory = CreateFactory(_upstream.Url, _upstream.Url);
        using var client = CreateNoRedirectClient(factory);

        var request = new HttpRequestMessage(HttpMethod.Head, "/");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(RootRedirectMiddleware.SpaRootPath, response.Headers.Location?.ToString());
        Assert.Null(_upstream.LastRequestPath);
    }

    /// <summary>
    /// A client that does not follow redirects, so a 302 is observable. The
    /// default factory client auto-follows the /spa/ redirect and would surface
    /// the final 200 instead of the 302 we assert on.
    /// </summary>
    private static HttpClient CreateNoRedirectClient(WebApplicationFactory<Program> factory)
        => new(factory.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost") };

    [Fact]
    public async Task Root_post_is_not_redirected_and_returns_404()
    {
        using var factory = CreateFactory(_upstream.Url, _upstream.Url);
        var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/");
        request.Content = new StringContent("", System.Text.Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);

        // The root is a page, not an API: only safe methods (GET/HEAD)
        // redirect. A POST is not redirected; with the monolith catch-all
        // removed (3d.2) it is a 404 from the gateway, not proxied.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(_upstream.LastRequestPath);
    }

    [Fact]
    public async Task Spa_root_is_proxied_to_frontend_and_not_redirected()
    {
        using var frontend = new StubUpstream("frontend");
        using var factory = CreateFactory(_upstream.Url, frontend.Url);
        var client = factory.CreateClient();

        using var response = await client.GetAsync("/spa/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("frontend-ok", await response.Content.ReadAsStringAsync());
        // The /spa prefix is stripped: the frontend sees the root path.
        Assert.Equal("/", frontend.LastRequestPath);
        Assert.Null(_upstream.LastRequestPath);
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string upstreamAddress,
        string frontendAddress,
        string? authAddress = null)
        => new TestAppFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Upstream:Monolith", upstreamAddress);
                builder.UseSetting("Upstream:Frontend", frontendAddress);
                // Required by the app (step 4.3b); unreachable unless a test
                // points it at a stub.
                builder.UseSetting("Upstream:Auth", authAddress ?? "http://127.0.0.1:1");
            });

    [Fact]
    public async Task Route_priority_auth_api_paths_go_to_auth()
    {
        using var frontend = new StubUpstream("frontend");
        using var auth = new StubUpstream("auth");
        using var factory = CreateFactory(_upstream.Url, frontend.Url, auth.Url);
        var client = factory.CreateClient();

        foreach (var path in new[] { "/api/auth/login", "/api/auth/register", "/api/auth/refresh", "/api/auth/logout", "/api/auth/anything/deeper" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("auth-ok", await response.Content.ReadAsStringAsync());
            Assert.Equal(path, auth.LastRequestPath);
            Assert.Null(_upstream.LastRequestPath);
            Assert.Null(frontend.LastRequestPath);
        }
    }

    [Fact]
    public async Task Route_priority_me_goes_to_auth()
    {
        using var frontend = new StubUpstream("frontend");
        using var auth = new StubUpstream("auth");
        using var factory = CreateFactory(_upstream.Url, frontend.Url, auth.Url);
        var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("auth-ok", await response.Content.ReadAsStringAsync());
        Assert.Equal("/api/me", auth.LastRequestPath);
        Assert.Null(_upstream.LastRequestPath);
    }

    [Fact]
    public async Task Route_me_lookalike_stays_on_monolith()
    {
        // The /api/me route must match the exact path, not a prefix:
        // /api/meetings (a hypothetical monolith route) must not leak to Auth.
        using var frontend = new StubUpstream("frontend");
        using var auth = new StubUpstream("auth");
        using var factory = CreateFactory(_upstream.Url, frontend.Url, auth.Url);
        var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/meetings");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("monolith-ok", await response.Content.ReadAsStringAsync());
        Assert.Equal("/api/meetings", _upstream.LastRequestPath);
        Assert.Null(auth.LastRequestPath);
    }

    [Fact]
    public async Task Auth_set_cookie_headers_pass_through_the_gateway_unchanged()
    {
        // Step 4.3b cookie-delivery check: the auth service sets the jwt
        // cookie (Path=/) and the rt cookie (Path=/api/auth) in its response;
        // the gateway must forward Set-Cookie verbatim (no path/flag rewrite),
        // otherwise the SPA session breaks.
        using var frontend = new StubUpstream("frontend");
        using var auth = new StubUpstream("auth");
        auth.SetResponse(
            "/api/auth/login",
            200,
            "auth-ok",
            ("Set-Cookie", "jwt=abc.def.ghi; Path=/; Max-Age=900; HttpOnly; Secure; SameSite=Lax"),
            ("Set-Cookie", "rt=refresh-token-value; Path=/api/auth; Max-Age=2592000; HttpOnly; Secure; SameSite=Strict"));
        using var factory = CreateFactory(_upstream.Url, frontend.Url, auth.Url);
        var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
        request.Headers.Add("Origin", "http://localhost:8080");
        request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var setCookies = response.Headers.GetValues("Set-Cookie").ToArray();
        var joined = string.Join("\n", setCookies);
        Assert.Contains(
            "jwt=abc.def.ghi; Path=/; Max-Age=900; HttpOnly; Secure; SameSite=Lax", joined);
        Assert.Contains(
            "rt=refresh-token-value; Path=/api/auth; Max-Age=2592000; HttpOnly; Secure; SameSite=Strict", joined);
    }

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
            // Required by the app (step 4.3b); tests that do not exercise the
            // auth routes keep the auth upstream unreachable. Tests that do
            // override it through the settings ("Upstream:Auth", stub.Url).
            builder.UseSetting("Upstream:Auth", "http://127.0.0.1:1");
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
    public async Task Post_api_auth_login_with_matching_origin_reaches_auth_upstream()
    {
        // Step 4.3b: the auth routes must pass the same-origin check for
        // normal SPA requests (a valid public Origin) — the check protects
        // the auth endpoints, it must not block them.
        using var auth = new StubUpstream("auth");
        using var factory = CreateFactory(_upstream.Url, ("PublicOrigin", PublicOrigin), ("Upstream:Auth", auth.Url));
        var client = factory.CreateClient();

        var request = Mutation(HttpMethod.Post, "/api/auth/login");
        request.Headers.Add("Origin", PublicOrigin);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/api/auth/login", auth.LastRequestPath);
        Assert.Null(_upstream.LastRequestPath);
    }

    [Fact]
    public async Task Post_api_auth_login_with_foreign_origin_returns_403_and_reaches_no_upstream()
    {
        using var auth = new StubUpstream("auth");
        using var factory = CreateFactory(_upstream.Url, ("PublicOrigin", PublicOrigin), ("Upstream:Auth", auth.Url));
        var client = factory.CreateClient();

        var request = Mutation(HttpMethod.Post, "/api/auth/login");
        request.Headers.Add("Origin", "http://evil.example");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, auth.CountOf("/api/auth/login"));
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
    public async Task Non_api_mutation_is_not_origin_checked()
    {
        using var factory = CreateFactory(_upstream.Url, ("PublicOrigin", PublicOrigin));
        var client = factory.CreateClient();

        // 3d.2 removed the Razor form posts (and the monolith catch-all). The
        // Origin check still applies only to /api/**: a non-/api mutation with
        // a foreign Origin is not Origin-rejected (no 403); with no catch-all
        // it is a 404 from the gateway.
        var request = Mutation(HttpMethod.Post, "/Account/Login");
        request.Headers.Add("Origin", "http://evil.example");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(_upstream.LastRequestPath);
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

public sealed class AuthRateLimitTests : IDisposable
{
    private const string PublicOrigin = "http://allowed.example";

    private readonly StubUpstream _upstream = new("monolith");

    private static WebApplicationFactory<Program> CreateFactory(string upstreamAddress, params (string Key, string Value)[] settings)
    {
        var factory = new TestAppFactory();
        return factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("WEB_PORT", "8080");
            builder.UseSetting("PublicOrigin", PublicOrigin);
            builder.UseSetting("Upstream:Monolith", upstreamAddress);
            // Step 4.3b: /api/auth/** is routed to the auth cluster; point it
            // at the same stub so the OK assertions (before the limit kicks in)
            // succeed.
            builder.UseSetting("Upstream:Auth", upstreamAddress);
            builder.UseSetting("Upstream:Frontend", "http://127.0.0.1:1");
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });
    }

    private static HttpRequestMessage AuthPost(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("Origin", PublicOrigin);
        request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        return request;
    }

    [Fact]
    public async Task Post_refresh_beyond_limit_returns_429()
    {
        using var factory = CreateFactory(_upstream.Url, ("RATE_LIMIT_REFRESH_REQUESTS_PER_MINUTE", "2"));
        var client = factory.CreateClient();

        for (var i = 0; i < 2; i++)
        {
            using var ok = await client.SendAsync(AuthPost("/api/auth/refresh"));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var rejected = await client.SendAsync(AuthPost("/api/auth/refresh"));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        var body = await rejected.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":429", body);
        Assert.Equal(2, _upstream.CountOf("/api/auth/refresh"));
    }

    [Fact]
    public async Task Refresh_uses_its_own_limit_even_when_login_limit_is_exhausted()
    {
        // The sliding-window bucket is per client (shared by all protected
        // paths), but the LIMIT applied depends on the path. Here the
        // login/register limit is 1 and the refresh limit is 2: after one
        // login, further logins are rejected — yet refresh is still allowed
        // because it is checked against its own higher limit. If refresh
        // mistakenly used the login limit, the first refresh would already
        // be a 429 (the shared bucket count is 1 >= 1).
        using var factory = CreateFactory(
            _upstream.Url,
            ("RATE_LIMIT_REQUESTS_PER_MINUTE", "1"),
            ("RATE_LIMIT_REFRESH_REQUESTS_PER_MINUTE", "2"));
        var client = factory.CreateClient();

        using var loginOk = await client.SendAsync(AuthPost("/api/auth/login"));
        Assert.Equal(HttpStatusCode.OK, loginOk.StatusCode);

        // The login/register limit (1) is now exhausted.
        using var loginRejected = await client.SendAsync(AuthPost("/api/auth/login"));
        Assert.Equal(HttpStatusCode.TooManyRequests, loginRejected.StatusCode);

        // Refresh is checked against its own limit (2): the first one passes,
        // the second exhausts the shared bucket.
        using var refreshOne = await client.SendAsync(AuthPost("/api/auth/refresh"));
        Assert.Equal(HttpStatusCode.OK, refreshOne.StatusCode);
        using var refreshTwo = await client.SendAsync(AuthPost("/api/auth/refresh"));
        Assert.Equal(HttpStatusCode.TooManyRequests, refreshTwo.StatusCode);
    }

    [Fact]
    public async Task Refresh_default_limit_is_thirty_per_window()
    {
        // No explicit setting: refresh allows 30 requests per window, and the
        // 31st is rejected. (Login would have rejected the 11th already.)
        using var factory = CreateFactory(_upstream.Url);
        var client = factory.CreateClient();

        for (var i = 0; i < 30; i++)
        {
            using var ok = await client.SendAsync(AuthPost("/api/auth/refresh"));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var rejected = await client.SendAsync(AuthPost("/api/auth/refresh"));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [Fact]
    public async Task Refresh_env_spelling_is_read()
    {
        // TD-24 class of bug: the env spelling must work, not only the
        // PascalCase appsettings key.
        var configEnvOnly = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RATE_LIMIT_REFRESH_REQUESTS_PER_MINUTE"] = "42",
            })
            .Build();
        Assert.Equal(
            42,
            AuthRateLimitMiddleware.ReadPositiveInt(
                configEnvOnly,
                AuthRateLimitMiddleware.RefreshRequestsPerMinuteSetting,
                AuthRateLimitMiddleware.RefreshRequestsPerMinuteEnv,
                AuthRateLimitMiddleware.DefaultRefreshRequestsPerMinute));

        var configPascalOnly = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimitRefreshRequestsPerMinute"] = "7",
            })
            .Build();
        Assert.Equal(
            7,
            AuthRateLimitMiddleware.ReadPositiveInt(
                configPascalOnly,
                AuthRateLimitMiddleware.RefreshRequestsPerMinuteSetting,
                AuthRateLimitMiddleware.RefreshRequestsPerMinuteEnv,
                AuthRateLimitMiddleware.DefaultRefreshRequestsPerMinute));

        // Unset: the default is 30.
        var configEmpty = new ConfigurationBuilder().Build();
        Assert.Equal(
            30,
            AuthRateLimitMiddleware.ReadPositiveInt(
                configEmpty,
                AuthRateLimitMiddleware.RefreshRequestsPerMinuteSetting,
                AuthRateLimitMiddleware.RefreshRequestsPerMinuteEnv,
                AuthRateLimitMiddleware.DefaultRefreshRequestsPerMinute));
    }

    [Fact]
    public async Task Post_login_beyond_limit_returns_429_problem_details_with_retry_after()
    {
        using var factory = CreateFactory(_upstream.Url, ("RATE_LIMIT_REQUESTS_PER_MINUTE", "3"));
        var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            using var ok = await client.SendAsync(AuthPost("/api/auth/login"));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var rejected = await client.SendAsync(AuthPost("/api/auth/login"));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        var retryAfter = rejected.Headers.RetryAfter?.Delta;
        Assert.NotNull(retryAfter);
        Assert.InRange(retryAfter!.Value.TotalSeconds, 1, 60);
        var body = await rejected.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":429", body);
        Assert.Contains("\"title\":\"Too Many Requests\"", body);
        // Only the first three requests reached the monolith.
        Assert.Equal(3, _upstream.CountOf("/api/auth/login"));
    }

    [Fact]
    public async Task Post_register_beyond_limit_returns_429()
    {
        using var factory = CreateFactory(_upstream.Url, ("RATE_LIMIT_REQUESTS_PER_MINUTE", "2"));
        var client = factory.CreateClient();

        for (var i = 0; i < 2; i++)
        {
            using var ok = await client.SendAsync(AuthPost("/api/auth/register"));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var rejected = await client.SendAsync(AuthPost("/api/auth/register"));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(2, _upstream.CountOf("/api/auth/register"));
    }

    [Fact]
    public async Task Different_clients_have_independent_buckets()
    {
        // Middleware-level test: TestServer does not set a per-client
        // RemoteIpAddress, so the per-client behavior is exercised directly
        // with distinct socket peers (the XFF / trusted-proxy key selection
        // is covered by ClientKeyResolverTests).
        var counter = new SlidingWindowCounter(TimeSpan.FromSeconds(60));
        var resolver = new TrustedProxyClientKeyResolver(Array.Empty<IPAddress>(), Array.Empty<IPNetwork>());
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimitRequestsPerMinute"] = "2",
            })
            .Build();
        var middleware = new AuthRateLimitMiddleware(
            _ => Task.CompletedTask,
            counter,
            resolver,
            config,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthRateLimitMiddleware>.Instance);

        async Task<int> Invoke(IPAddress remote)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = "/api/auth/login";
            context.Connection.RemoteIpAddress = remote;
            await middleware.InvokeAsync(context);
            return context.Response.StatusCode;
        }

        var clientA = IPAddress.Parse("192.0.2.1");
        var clientB = IPAddress.Parse("198.51.100.9");

        Assert.Equal(200, await Invoke(clientA));
        Assert.Equal(200, await Invoke(clientA));
        Assert.Equal(429, await Invoke(clientA));

        // A different client has a fresh bucket: the limit is per client, not global.
        Assert.Equal(200, await Invoke(clientB));
        Assert.Equal(200, await Invoke(clientB));
        Assert.Equal(429, await Invoke(clientB));
    }

    [Fact]
    public async Task Rate_limit_does_not_apply_to_other_routes()
    {
        using var factory = CreateFactory(_upstream.Url, ("RATE_LIMIT_REQUESTS_PER_MINUTE", "1"));
        var client = factory.CreateClient();

        using var first = await client.SendAsync(AuthPost("/api/auth/login"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Other /api routes are not limited by the auth counter.
        var other = AuthPost("/api/models");
        using var second = await client.SendAsync(other);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("/api/models", _upstream.LastRequestPath);

        // The login counter is exhausted: the next login attempt is rejected.
        using var third = await client.SendAsync(AuthPost("/api/auth/login"));
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }

    [Fact]
    public async Task Non_post_requests_to_login_path_are_not_limited()
    {
        using var factory = CreateFactory(_upstream.Url, ("RATE_LIMIT_REQUESTS_PER_MINUTE", "1"));
        var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            using var response = await client.GetAsync("/api/auth/login");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(3, _upstream.CountOf("/api/auth/login"));
    }

    [Fact]
    public async Task Limit_setting_via_env_spelling_is_read()
    {
        // Regression guard (TD-24 class of bug): the env spelling must work,
        // not only the PascalCase appsettings key.
        using var factory = CreateFactory(_upstream.Url, ("RATE_LIMIT_WINDOW_SECONDS", "90"));
        // The window spelling is exercised by the counter construction at
        // startup; if the env spelling were ignored the default 60 s window
        // would be used and this test would not detect it here, so also verify
        // the helper directly against the same config the app sees.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RATE_LIMIT_REQUESTS_PER_MINUTE"] = "5",
                ["RateLimitRequestsPerMinute"] = "7",
            })
            .Build();
        Assert.Equal(7, AuthRateLimitMiddleware.ReadPositiveInt(config, "RateLimitRequestsPerMinute", "RATE_LIMIT_REQUESTS_PER_MINUTE", 10));
        var configEnvOnly = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RATE_LIMIT_REQUESTS_PER_MINUTE"] = "5",
            })
            .Build();
        Assert.Equal(5, AuthRateLimitMiddleware.ReadPositiveInt(configEnvOnly, "RateLimitRequestsPerMinute", "RATE_LIMIT_REQUESTS_PER_MINUTE", 10));

        var client = factory.CreateClient();
        for (var i = 0; i < 10; i++)
        {
            using var ok = await client.SendAsync(AuthPost("/api/auth/login"));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var rejected = await client.SendAsync(AuthPost("/api/auth/login"));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    public void Dispose() => _upstream.Dispose();
}

public sealed class ClientKeyResolverTests
{
    private static TrustedProxyClientKeyResolver Resolver(
        params (IPAddress Ip, IPNetwork? Network)[] entries)
    {
        var ips = entries.Where(e => e.Network is null).Select(e => e.Ip).ToArray();
        var networks = entries.Where(e => e.Network is not null).Select(e => e.Network!.Value).ToArray();
        return new TrustedProxyClientKeyResolver(ips, networks);
    }

    [Fact]
    public void Untrusted_remote_ignores_x_forwarded_for()
    {
        var resolver = Resolver();
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.7");
        context.Request.Headers["X-Forwarded-For"] = "1.1.1.1";

        Assert.Equal("192.0.2.7", resolver.Resolve(context));
    }

    [Fact]
    public void Trusted_remote_without_xff_uses_the_peer()
    {
        var resolver = Resolver((IPAddress.Parse("192.0.2.7"), null));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.7");

        Assert.Equal("192.0.2.7", resolver.Resolve(context));
    }

    [Fact]
    public void Trusted_remote_walks_xff_right_to_left_skipping_trusted_entries()
    {
        var resolver = Resolver(
            (IPAddress.Parse("10.0.0.5"), null),
            (IPAddress.Any, IPNetwork.Parse("172.16.0.0/12")));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.23, 172.16.4.4, 10.0.0.5";

        // 10.0.0.5 (trusted proxy) is skipped, 172.16.4.4 (in 172.16/12) is
        // skipped, 198.51.100.23 is the first untrusted entry: the client.
        Assert.Equal("198.51.100.23", resolver.Resolve(context));
    }

    [Fact]
    public void Trusted_remote_with_all_trusted_xff_falls_back_to_leftmost_entry()
    {
        var resolver = Resolver((IPAddress.Any, IPNetwork.Parse("172.16.0.0/12")));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("172.16.9.9");
        context.Request.Headers["X-Forwarded-For"] = "172.16.1.1, 172.16.2.2";

        Assert.Equal("172.16.1.1", resolver.Resolve(context));
    }

    [Fact]
    public void Unparseable_xff_entries_are_skipped()
    {
        var resolver = Resolver((IPAddress.Parse("10.0.0.5"), null));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");
        context.Request.Headers["X-Forwarded-For"] = "garbage, 203.0.113.4, 10.0.0.5";

        Assert.Equal("203.0.113.4", resolver.Resolve(context));
    }

    [Fact]
    public void Null_remote_returns_unknown()
    {
        var resolver = Resolver();
        var context = new DefaultHttpContext();

        Assert.Equal("unknown", resolver.Resolve(context));
    }

    [Fact]
    public void FromConfiguration_parses_proxies_and_networks()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KNOWN_PROXIES"] = "1.1.1.1, 2.2.2.2",
                ["KNOWN_NETWORKS"] = "10.0.0.0/8",
            })
            .Build();
        var resolver = TrustedProxyClientKeyResolver.FromConfiguration(config);

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("2.2.2.2");
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.9, 10.1.2.3";
        Assert.Equal("198.51.100.9", resolver.Resolve(context));
    }

    [Fact]
    public void FromConfiguration_with_no_settings_trusts_nothing()
    {
        var config = new ConfigurationBuilder().Build();
        var resolver = TrustedProxyClientKeyResolver.FromConfiguration(config);

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.7");
        context.Request.Headers["X-Forwarded-For"] = "1.1.1.1";
        Assert.Equal("192.0.2.7", resolver.Resolve(context));
    }

    [Fact]
    public void FromConfiguration_rejects_unparseable_proxy()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KNOWN_PROXIES"] = "not-an-ip",
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            TrustedProxyClientKeyResolver.FromConfiguration(config));
        Assert.Contains("KNOWN_PROXIES", ex.Message);
    }

    [Fact]
    public void FromConfiguration_rejects_unparseable_network()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KNOWN_NETWORKS"] = "10.0.0.0/bad",
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            TrustedProxyClientKeyResolver.FromConfiguration(config));
        Assert.Contains("KNOWN_NETWORKS", ex.Message);
    }

    /// <summary>
    /// Builds the IPv4-mapped IPv6 form (::ffff:a.b.c.d) that Kestrel on
    /// Linux reports as the socket peer for IPv4 connections.
    /// </summary>
    private static IPAddress V4Mapped(string ip)
    {
        var v4 = IPAddress.Parse(ip).GetAddressBytes();
        var mapped = new byte[16];
        mapped[10] = 0xff;
        mapped[11] = 0xff;
        Array.Copy(v4, 0, mapped, 12, 4);
        return new IPAddress(mapped);
    }

    [Fact]
    public void V4_mapped_remote_matching_known_proxy_is_trusted()
    {
        // Regression: Kestrel on Linux reports the peer as ::ffff:172.28.0.10,
        // while KNOWN_PROXIES holds the plain IPv4 172.28.0.10. Without the
        // canonicalization the proxy was silently untrusted and XFF ignored.
        var resolver = Resolver((IPAddress.Parse("172.28.0.10"), null));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = V4Mapped("172.28.0.10");
        context.Request.Headers["X-Forwarded-For"] = "192.168.1.50";

        Assert.Equal("192.168.1.50", resolver.Resolve(context));
    }

    [Fact]
    public void V4_mapped_untrusted_remote_ignores_xff_and_key_is_canonicalized()
    {
        // The host bridge address is not trusted: the forged XFF must be
        // ignored, and the key must be the plain IPv4 form (not ::ffff:172.28.0.1)
        // so it matches the key of the same client seen as plain IPv4.
        var resolver = Resolver((IPAddress.Parse("172.28.0.10"), null));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = V4Mapped("172.28.0.1");
        context.Request.Headers["X-Forwarded-For"] = "9.9.9.9";

        Assert.Equal("172.28.0.1", resolver.Resolve(context));
    }

    [Fact]
    public void V4_mapped_remote_in_known_network_is_trusted()
    {
        var resolver = Resolver((IPAddress.Any, IPNetwork.Parse("172.28.0.0/16")));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = V4Mapped("172.28.0.7");
        context.Request.Headers["X-Forwarded-For"] = "192.168.1.50, 172.28.0.10";

        Assert.Equal("192.168.1.50", resolver.Resolve(context));
    }

    [Fact]
    public void V4_mapped_xff_entries_are_canonicalized_before_trust_check()
    {
        // A trusted proxy chain that carries the client in mapped form must
        // still be recognized as a proxy and skipped.
        var resolver = Resolver((IPAddress.Parse("172.28.0.10"), null));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = V4Mapped("172.28.0.10");
        context.Request.Headers["X-Forwarded-For"] = "192.168.1.50, ::ffff:172.28.0.10";

        Assert.Equal("192.168.1.50", resolver.Resolve(context));
    }
}

public sealed class SlidingWindowCounterTests
{
    [Fact]
    public void Allows_up_to_limit_within_window_then_rejects()
    {
        var nowSeconds = 0L;
        var counter = new SlidingWindowCounter(TimeSpan.FromSeconds(10), () => nowSeconds * TimeSpan.TicksPerSecond);

        Assert.True(counter.TryAcquire("a", 2, out _));
        Assert.True(counter.TryAcquire("a", 2, out _));
        Assert.False(counter.TryAcquire("a", 2, out var retryAfter));
        Assert.InRange(retryAfter, 1, 10);
    }

    [Fact]
    public void Expired_timestamps_leave_room_for_new_acquisitions()
    {
        var nowSeconds = 0L;
        var counter = new SlidingWindowCounter(TimeSpan.FromSeconds(10), () => nowSeconds * TimeSpan.TicksPerSecond);

        Assert.True(counter.TryAcquire("a", 1, out _));

        nowSeconds = 9; // The first timestamp is still inside the window.
        Assert.False(counter.TryAcquire("a", 1, out _));

        nowSeconds = 10; // It has just expired: room opens up.
        Assert.True(counter.TryAcquire("a", 1, out _));
    }

    [Fact]
    public void Distinct_keys_are_independent()
    {
        var counter = new SlidingWindowCounter(TimeSpan.FromSeconds(10));

        Assert.True(counter.TryAcquire("a", 1, out _));
        Assert.False(counter.TryAcquire("a", 1, out _));
        Assert.True(counter.TryAcquire("b", 1, out _));
    }

    [Fact]
    public void Retry_after_is_never_below_one_second()
    {
        var nowSeconds = 0L;
        var counter = new SlidingWindowCounter(TimeSpan.FromSeconds(10), () => nowSeconds * TimeSpan.TicksPerSecond);

        Assert.True(counter.TryAcquire("a", 1, out _));
        nowSeconds = 9; // Oldest timestamp expires in exactly one second.
        Assert.False(counter.TryAcquire("a", 1, out var retryAfter));
        Assert.Equal(1, retryAfter);
    }

    [Fact]
    public void Non_positive_window_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new SlidingWindowCounter(TimeSpan.Zero));
    }

    [Fact]
    public async Task Default_clock_advances_in_ticks_units_so_windows_expire()
    {
        // Regression: the default clock used to feed millisecond values
        // (Environment.TickCount64) into tick-based arithmetic, so a window
        // never expired within any realistic test or production timeframe.
        var counter = new SlidingWindowCounter(TimeSpan.FromMilliseconds(150));

        Assert.True(counter.TryAcquire("a", 1, out _));
        Assert.False(counter.TryAcquire("a", 1, out _));

        await Task.Delay(250);

        Assert.True(counter.TryAcquire("a", 1, out _));
    }
}

/// <summary>
/// Minimal Kestrel-based HTTP stub that records the headers of the last request
/// and answers 200. Kestrel (like the real monolith) accepts any Host header.
/// </summary>
internal sealed class StubUpstream : IDisposable
{
    private readonly WebApplication _app;
    private readonly string _marker;
    private readonly List<string> _requestPaths = [];
    private readonly ConcurrentDictionary<string, (int Status, string? Body, (string Name, string Value)[]? Headers)> _pathResponses =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Makes the stub return a fixed status for the given path (used by the
    /// /health/ready tests: e.g. /api/health -> 404 or 500). Paths without an
    /// entry return 200 with the usual "{marker}-ok" body.
    /// </summary>
    public void SetStatus(string path, int status)
    {
        _pathResponses[path] = (status, null, null);
    }

    /// <summary>
    /// Makes the stub answer the given path with an explicit status, body and
    /// response headers (step 4.3b: verifying that Set-Cookie from Auth
    /// passes the Gateway through unchanged).
    /// </summary>
    public void SetResponse(string path, int status, string? body, params (string Name, string Value)[] headers)
    {
        _pathResponses[path] = (status, body, headers);
    }

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
            var path = context.Request.Path.Value ?? string.Empty;
            _requestPaths.Add(path);
            var collection = new NameValueCollection();
            foreach (var (key, value) in context.Request.Headers)
            {
                collection[key] = value.ToString();
            }

            LastRequestHeaders = collection;
            LastRequestPath = path;

            if (_pathResponses.TryGetValue(path, out var configured))
            {
                context.Response.StatusCode = configured.Status;
                if (configured.Headers is not null)
                {
                    foreach (var (name, value) in configured.Headers)
                    {
                        context.Response.Headers.Append(name, value);
                    }
                }
                return context.Response.WriteAsync(configured.Body ?? $"{_marker}-ok");
            }

            return context.Response.WriteAsync($"{_marker}-ok");
        });
        _app.StartAsync().GetAwaiter().GetResult();
    }

    public string Url { get; }

    public NameValueCollection? LastRequestHeaders { get; private set; }

    public string? LastRequestPath { get; private set; }

    public int CountOf(string path) => _requestPaths.Count(p => p == path);

    public int TotalRequests => _requestPaths.Count;

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
