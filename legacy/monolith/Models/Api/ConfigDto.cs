namespace LoginAndReg.Models.Api;

/// <summary>
/// Client configuration for the SPA (GET /api/config, step 3c.4). Lets the
/// SPA validate uploads on the client side with the same limit the server
/// enforces (MAX_UPLOAD_BYTES, ADR 0015).
/// </summary>
public class ConfigDto
{
    /// <summary>Maximum upload size in bytes (shared with the Gateway).</summary>
    public long MaxUploadBytes { get; init; }

    /// <summary>
    /// File extensions the SPA upload accepts (the SPA is GLB-only).
    /// </summary>
    public string[] AllowedUploadExtensions { get; init; } = Array.Empty<string>();
}
