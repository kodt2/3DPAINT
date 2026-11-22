namespace LoginAndReg.Models.Options;

public class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Key { get; init; } = string.Empty;
    public int ExpiryMinutes { get; init; } = 60;
}
