using System.Security.Cryptography;
using System.Text;

namespace LoginAndReg.Services;

public class PasswordService : IPasswordService
{
    public string GenerateSalt()
    {
        var bytes = new byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    public string HashPassword(string password, string salt)
    {
        using var sha256 = SHA256.Create();
        var combined = Encoding.UTF8.GetBytes(password + salt);
        return Convert.ToBase64String(sha256.ComputeHash(combined));
    }

    public bool VerifyPassword(string password, string salt, string hash)
    {
        var calculatedHash = HashPassword(password, salt);
        return calculatedHash == hash;
    }
}
