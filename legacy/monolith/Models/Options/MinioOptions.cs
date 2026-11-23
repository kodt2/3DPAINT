namespace LoginAndReg.Models.Options;

public class MinioOptions
{
    public const string SectionName = "Minio";

    public string Endpoint { get; set; } = string.Empty;
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string BucketName { get; set; } = "user-data";
    public string Region { get; set; } = string.Empty;
    public bool UseSsl { get; set; }
    public int PresignedUrlExpiryMinutes { get; set; } = 30;

    /// <summary>
    /// Public S3 endpoint used to sign presigned URLs (TD-3).
    /// Data operations always use <see cref="Endpoint"/> (internal network);
    /// when this value is empty, presigned URLs fall back to it as well.
    /// </summary>
    public string PublicEndpoint { get; set; } = string.Empty;
}
