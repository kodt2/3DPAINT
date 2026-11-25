namespace LoginAndReg.Models.Options;

/// <summary>
/// Upload limits and the accepted extensions. Single source of truth: the
/// compose variables (ADR 0015) — the Gateway reads the same limit, and both
/// the server-side validation (FileStorageService) and the public /api/config
/// read the same extension list from here, so the SPA and the server can
/// never disagree.
/// </summary>
public class UploadOptions
{
    /// <summary>
    /// Default limit when MAX_UPLOAD_BYTES is not set (100 MiB — the same
    /// default the Gateway uses).
    /// </summary>
    public const long DefaultMaxBytes = 100L * 1024L * 1024L;

    /// <summary>
    /// Multipart envelope headroom (boundary, headers, form fields) so a
    /// file exactly at the limit passes the body-level checks and is
    /// rejected by the app-level check with a human-readable 413.
    /// </summary>
    public const long BodySlack = 1L * 1024L * 1024L;

    /// <summary>
    /// Default accepted extensions when ALLOWED_UPLOAD_EXTENSIONS is not set.
    /// GLB only (the SPA viewer is GLB-only, ADR 0015).
    /// </summary>
    public const string DefaultAllowedExtensions = ".glb";

    /// <summary>
    /// Maximum size of an uploaded file, in bytes.
    /// </summary>
    public long MaxBytes { get; set; } = DefaultMaxBytes;

    /// <summary>
    /// Accepted upload extensions (with a leading dot, e.g. ".glb"). Used by
    /// both the server-side validation and the public /api/config, so the SPA
    /// and the server enforce the same list (single source of truth).
    /// </summary>
    public IReadOnlyList<string> AllowedExtensions { get; set; } =
        DefaultAllowedExtensions.Split(',');
}
