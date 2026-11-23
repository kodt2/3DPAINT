using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace LoginAndReg.OpenApi;

/// <summary>
/// Keeps only /api/** operations in the OpenAPI document. The SPA client is
/// generated from this document; MVC page routes (/Account, /Home) are not part
/// of the API contract.
/// </summary>
public class ApiOnlyDocumentFilter : IDocumentFilter
{
    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        var toRemove = swaggerDoc.Paths.Keys
            .Where(k => !k.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var key in toRemove)
        {
            swaggerDoc.Paths.Remove(key);
        }
    }
}
