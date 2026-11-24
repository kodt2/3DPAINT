namespace LoginAndReg.Services;

/// <summary>
/// Thrown when an uploaded file exceeds the configured limit
/// (UploadOptions.MaxBytes, ADR 0015). Mapped to 413 by the API controller.
/// </summary>
public class FileSizeLimitExceededException : Exception
{
    public FileSizeLimitExceededException(long limitBytes)
        : base($"Размер файла превышает {FormatLimit(limitBytes)}.")
    {
        LimitBytes = limitBytes;
    }

    /// <summary>The limit the file was compared against, in bytes.</summary>
    public long LimitBytes { get; }

    public static string FormatLimit(long bytes)
    {
        if (bytes % (1024L * 1024L) == 0)
        {
            return $"{bytes / (1024L * 1024L)} МБ";
        }

        return $"{bytes} байт";
    }
}
