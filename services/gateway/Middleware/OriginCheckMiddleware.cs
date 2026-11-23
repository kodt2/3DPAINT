using System.Text;
using System.Text.Json;

namespace Gateway.Middleware;

/// <summary>
/// CSRF protection for state-changing requests (POST/PUT/PATCH/DELETE) to /api/**.
/// The request must carry an <c>Origin</c> header (or a <c>Referer</c> as a
/// fallback) whose origin matches the gateway's public origin. GET/HEAD/OPTIONS
/// are not state-changing and pass through untouched.
///
/// The allowed origin list comes from the <c>PublicOrigin</c> setting
/// (env <c>PUBLIC_ORIGIN</c>, comma-separated). When it is not configured the
/// default is <c>http://localhost:{WEB_PORT}</c> (WEB_PORT defaults to 8080).
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

    private static IReadOnlyList<string> ResolveAllowedOrigins(IConfiguration configuration)
    {
        var raw = configuration[AllowedOriginsSetting];
        if (string.IsNullOrWhiteSpace(raw))
        {
            var port = configuration[WebPortSetting];
            if (!int.TryParse(port, out var webPort) || webPort <= 0)
            {
                webPort = DefaultWebPort;
            }

            return [$"http://localhost:{webPort}"];
        }

        return raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(origin => origin.TrimEnd('/'))
            .Where(origin => origin.Length > 0)
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
    /// Origin of the requesting page: the Origin header, or the origin part of
    /// the Referer when Origin is absent. Null when neither is usable.
    /// </summary>
    private static string? ExtractRequestOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (!string.IsNullOrWhiteSpace(origin))
        {
            return origin.TrimEnd('/');
        }

        var referer = request.Headers.Referer.ToString();
        if (string.IsNullOrWhiteSpace(referer))
        {
            return null;
        }

        if (!Uri.TryCreate(referer, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return $"{uri.Scheme}://{uri.Authority}";
    }
}
