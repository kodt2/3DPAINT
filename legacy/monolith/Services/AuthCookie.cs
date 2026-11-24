using Microsoft.AspNetCore.Http;

namespace LoginAndReg.Services
{
    /// <summary>
    /// Shared definition of the SPA auth cookie (ADR 0011): name, and the
    /// SameSite=Lax + HttpOnly + Secure + Path=/ options that the JSON API
    /// (AuthApiController) must use so the cookie is identical to the one the
    /// legacy Razor flow (AccountController) sets.
    /// </summary>
    public static class AuthCookie
    {
        public const string Name = "jwt";

        public static CookieOptions CreateOptions() => new()
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
        };
    }
}
