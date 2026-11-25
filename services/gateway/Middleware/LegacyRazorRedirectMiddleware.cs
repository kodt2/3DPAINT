namespace Gateway.Middleware;

/// <summary>
/// Redirects the old Razor page routes to the SPA (step 3d.2). The Razor UI
/// (HomeController, AccountController) was removed from the monolith, so its
/// page routes /Home/** and /Account/** no longer exist there. GET/HEAD
/// requests to them are sent to the SPA root with a relative 302 Location
/// ("spa/"): the browser (and any upstream TLS proxy such as Caddy) resolves
/// it against the current scheme+host+port, so the redirect keeps the origin
/// it was issued from — including a non-standard HTTPS port — without the
/// gateway knowing the public address (same approach as
/// RootRedirectMiddleware, ADR 0016).
///
/// Only safe methods (GET/HEAD) are redirected. Non-safe methods to these
/// paths are not proxied anywhere: the monolith no longer has a catch-all
/// (step 3d.2), so they fall through to the gateway's 404.
/// </summary>
public sealed class LegacyRazorRedirectMiddleware
{
    public const string SpaRootPath = "/spa/";

    private static readonly string[] LegacyPagePrefixes = ["/Home", "/Account"];

    private readonly RequestDelegate _next;

    public LegacyRazorRedirectMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (
            (context.Request.Method == HttpMethods.Get || context.Request.Method == HttpMethods.Head)
            && IsLegacyPagePath(context.Request.Path)
        )
        {
            context.Response.StatusCode = StatusCodes.Status302Found;
            context.Response.Headers.Location = SpaRootPath;
            return Task.CompletedTask;
        }

        return _next(context);
    }

    private static bool IsLegacyPagePath(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var prefix in LegacyPagePrefixes)
        {
            // Match the prefix itself or a sub-path under it: "/Home",
            // "/Home/", "/Home/Index", "/Account", "/Account/Login", ...
            // The trailing "/" guard keeps lookalikes like "/Homepage" out.
            if (value == prefix || value.StartsWith(prefix + "/", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
