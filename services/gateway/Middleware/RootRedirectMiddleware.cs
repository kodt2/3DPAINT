namespace Gateway.Middleware;

/// <summary>
/// Redirects the site root to the SPA (step 3d.1). The Location header is a
/// relative path ("spa/"), never an absolute URL built from the request: the
/// browser (and any upstream TLS proxy such as Caddy) resolves it against the
/// current scheme+host+port, so the redirect keeps the origin it was issued
/// from — including a non-standard HTTPS port — without the gateway knowing
/// the public address (ADR 0016).
///
/// Only the exact root path is affected, and only for safe methods: the
/// monolith's Razor root is a page, not an API, and there is no meaningful
/// non-GET operation on "/".
/// </summary>
public sealed class RootRedirectMiddleware
{
    public const string SpaRootPath = "/spa/";

    private readonly RequestDelegate _next;

    public RootRedirectMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (
            (context.Request.Method == HttpMethods.Get || context.Request.Method == HttpMethods.Head)
            && context.Request.Path == "/"
        )
        {
            context.Response.StatusCode = StatusCodes.Status302Found;
            context.Response.Headers.Location = SpaRootPath;
            return Task.CompletedTask;
        }

        return _next(context);
    }
}
