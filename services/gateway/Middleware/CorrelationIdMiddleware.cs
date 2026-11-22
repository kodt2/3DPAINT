using Serilog.Context;

namespace Gateway.Middleware;

/// <summary>
/// Passes an incoming X-Correlation-Id through (if valid) or generates a new one,
/// puts it into the request (so the upstream sees it) and the response,
/// and scopes it into the log context.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";
    public const string ItemsKey = "CorrelationId";
    public const int MaxLength = 128;

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task Invoke(HttpContext context)
    {
        var correlationId = GetOrGenerate(context);
        context.Items[ItemsKey] = correlationId;
        // Set the request header so every downstream service (proxied or added
        // later) sees the same correlation id.
        context.Request.Headers[HeaderName] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context);
        }
    }

    public static string GetOrGenerate(HttpContext context)
    {
        var supplied = context.Request.Headers[HeaderName].ToString().Trim();
        if (supplied.Length > 0 && supplied.Length <= MaxLength && IsPrintableAscii(supplied))
        {
            return supplied;
        }

        return Guid.NewGuid().ToString("N");
    }

    private static bool IsPrintableAscii(string value)
    {
        foreach (var character in value)
        {
            if (character < 0x20 || character > 0x7E)
            {
                return false;
            }
        }

        return true;
    }
}
