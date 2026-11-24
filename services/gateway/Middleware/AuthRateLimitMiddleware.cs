using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Gateway.Middleware;

/// <summary>
/// Rate-limits the auth endpoints (POST /api/auth/login, POST /api/auth/register)
/// per client to blunt brute-force / credential-stuffing attempts.
///
/// The client key comes from <see cref="IClientKeyResolver"/>: the socket peer,
/// or the X-Forwarded-For client when the connection comes from a trusted proxy
/// (KNOWN_PROXIES / KNOWN_NETWORKS).
///
/// The limit runs <b>before</b> the CSRF origin check on purpose: a flood of
/// bad-origin requests is still a flood.
/// </summary>
public sealed class AuthRateLimitMiddleware
{
    public const string RequestsPerMinuteSetting = "RateLimitRequestsPerMinute";
    public const string RequestsPerMinuteEnv = "RATE_LIMIT_REQUESTS_PER_MINUTE";
    public const string WindowSecondsSetting = "RateLimitWindowSeconds";
    public const string WindowSecondsEnv = "RATE_LIMIT_WINDOW_SECONDS";
    public const int DefaultRequestsPerMinute = 10;
    public const int DefaultWindowSeconds = 60;

    private static readonly HashSet<string> ProtectedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/auth/login",
        "/api/auth/register",
    };

    private readonly RequestDelegate _next;
    private readonly SlidingWindowCounter _counter;
    private readonly IClientKeyResolver _clientKey;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthRateLimitMiddleware> _logger;

    public AuthRateLimitMiddleware(
        RequestDelegate next,
        SlidingWindowCounter counter,
        IClientKeyResolver clientKey,
        IConfiguration configuration,
        ILogger<AuthRateLimitMiddleware> logger)
    {
        _next = next;
        _counter = counter;
        _clientKey = clientKey;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method) || !ProtectedPaths.Contains(context.Request.Path.Value ?? string.Empty))
        {
            await _next(context);
            return;
        }

        var limit = ReadPositiveInt(_configuration, RequestsPerMinuteSetting, RequestsPerMinuteEnv, DefaultRequestsPerMinute);
        var key = _clientKey.Resolve(context);

        if (_counter.TryAcquire(key, limit, out var retryAfterSeconds))
        {
            await _next(context);
            return;
        }

        _logger.LogWarning("Auth rate limit exceeded for {Client} on {Path}", key, context.Request.Path.Value);

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.ContentType = "application/problem+json; charset=utf-8";
        context.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        // WriteAsync (not WriteAsJsonAsync): the latter overwrites Content-Type.
        await context.Response.WriteAsync(
            JsonSerializer.Serialize(new
            {
                type = "https://httpwg.org/specs/rfc7807#too-many-requests",
                title = "Too Many Requests",
                status = 429,
                detail = "Too many login or registration attempts. Try again in a moment.",
            }),
            Encoding.UTF8);
    }

    /// <summary>
    /// Reads a positive integer setting. The appsettings key and the env var
    /// spelling differ — the environment provider registers variables under
    /// their exact name and config lookup is case-sensitive (see TD-24).
    /// </summary>
    public static int ReadPositiveInt(IConfiguration configuration, string setting, string env, int fallback)
    {
        var raw = configuration[setting] ?? configuration[env];
        return int.TryParse(raw, out var value) && value > 0 ? value : fallback;
    }
}
