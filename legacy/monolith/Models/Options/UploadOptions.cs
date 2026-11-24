namespace LoginAndReg.Models.Options;

/// <summary>
/// Upload limits. Single source of truth: the MAX_UPLOAD_BYTES compose
/// variable (ADR 0015) — the Gateway reads the same variable, so both edges
/// enforce the same limit.
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
    /// Maximum size of an uploaded file, in bytes.
    /// </summary>
    public long MaxBytes { get; set; } = DefaultMaxBytes;
}
