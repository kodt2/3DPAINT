namespace LoginAndReg.Models.Options;

public class MinioOptions
{
    public const string SectionName = "Minio";

    public string Endpoint { get; set; } = string.Empty;
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string BucketName { get; set; } = "user-data";
    public bool UseSsl { get; set; }
    public int PresignedUrlExpiryMinutes { get; set; } = 30;
}
