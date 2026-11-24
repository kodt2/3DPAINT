using System.Text;
using System.Text.Json;

namespace Gateway.Middleware;

/// <summary>
/// CSRF protection for state-changing requests (POST/PUT/PATCH/DELETE) to /api/**.
/// The request must carry an <c>Origin</c> header (or a <c>Referer</c> as a
/// fallback) whose origin matches the gateway's public origin. GET/HEAD/OPTIONS
/// are not state-changing and pass through untouched.
///
/// Origins are compared as parsed URLs (scheme + host + effective port), never
/// as string prefixes: "http://localhost:8080.evil.com" is not an origin that
/// starts with "http://localhost:8080", and "https://example.com:443" equals
/// "https://example.com" (443 is the default port). The literal Origin value
/// "null" (opaque origins) does not parse as a URL and is rejected.
///
/// The allowed origin list comes from the <c>PublicOrigin</c> setting
/// (env <c>PUBLIC_ORIGIN</c>, comma-separated). When it is not configured the
/// default is <c>http://localhost:{WEB_PORT}</c> (WEB_PORT defaults to 8080).
/// In the dev profile (make up-dev) the Vite dev server origin
/// (http://localhost:5173) is added via infra/compose.dev.yml.
/// See ADR 0011 («CSRF-защита») for the rationale.
/// </summary>
public sealed class OriginCheckMiddleware
{
    private const string AllowedOriginsSetting = "PublicOrigin";
    private const string WebPortSetting = "WEB_PORT";
    private const int DefaultWebPort = 8080;

    private readonly RequestDelegate _next;
    private readonly IReadOnlyList<string> _allowedOrigins;

    public OriginCheckMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        _allowedOrigins = ResolveAllowedOrigins(configuration);
    }

    /// <summary>
    /// Normalizes an origin to "scheme://host:port" with the effective port
    /// (default ports included: http→80, https→443). Returns null when the
    /// value is not an absolute http(s) URL — including the literal "null".
    /// </summary>
    internal static string? NormalizeOrigin(string candidate)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        return $"{uri.Scheme}://{uri.IdnHost}:{uri.Port}";
    }

    private static IReadOnlyList<string> ResolveAllowedOrigins(IConfiguration configuration)
    {
        // Two spellings on purpose: "PublicOrigin" is the appsettings key,
        // "PUBLIC_ORIGIN" is the environment variable name (the environment
        // provider registers env vars under their exact name, not under
        // PascalCase keys — the lookup is case-sensitive in .NET 10).
        // Reading only one of them would silently ignore the other.
        var raw = configuration[AllowedOriginsSetting] ?? configuration["PUBLIC_ORIGIN"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            var port = configuration[WebPortSetting];
            if (!int.TryParse(port, out var webPort) || webPort <= 0)
            {
                webPort = DefaultWebPort;
            }

            return [NormalizeOrigin($"http://localhost:{webPort}")!];
        }

        return raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeOrigin)
            .Where(origin => origin is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsStateChangingApiRequest(context.Request))
        {
            await _next(context);
            return;
        }

        var origin = ExtractRequestOrigin(context.Request);
        if (origin is not null && _allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        await WriteForbiddenAsync(context);
    }

    private static async Task WriteForbiddenAsync(HttpContext context)
    {
        // RFC 7807 JSON with the canonical lower-case field names; the shape
        // matches the monolith problem responses (see ADR 0011).
        // WriteAsync (not WriteAsJsonAsync): the latter replaces the
        // Content-Type with application/json and would drop the
        // application/problem+json media type.
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/problem+json; charset=utf-8";

        var problem = new
        {
            type = "https://httpwg.org/specs/rfc7807#about-blank",
            title = "Forbidden",
            status = StatusCodes.Status403Forbidden,
            detail = "The request Origin (or Referer) does not match the public origin of the gateway.",
        };
        var json = JsonSerializer.Serialize(problem, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await context.Response.WriteAsync(json, Encoding.UTF8);
    }

    private static bool IsStateChangingApiRequest(HttpRequest request)
    {
        if (!request.Path.StartsWithSegments("/api"))
        {
            return false;
        }

        var method = request.Method;
        return HttpMethods.IsPost(method) || HttpMethods.IsPut(method)
               || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);
    }

    /// <summary>
    /// Origin of the requesting page as a normalized "scheme://host:port"
    /// string: the Origin header, or the origin part of the Referer when
    /// Origin is absent. Null when neither is a usable absolute http(s) URL
    /// (absent headers, or the literal "null" origin).
    /// </summary>
    private static string? ExtractRequestOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (!string.IsNullOrWhiteSpace(origin))
        {
            return NormalizeOrigin(origin);
        }

        var referer = request.Headers.Referer.ToString();
        if (string.IsNullOrWhiteSpace(referer))
        {
            return null;
        }

        return NormalizeOrigin(referer);
    }
}
