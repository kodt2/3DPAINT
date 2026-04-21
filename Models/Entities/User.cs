namespace LoginAndReg.Models.Entities;

public class User
{
    public int id { get; set; }
    public string login { get; set; } = string.Empty;
    public string password_hash { get; set; } = string.Empty;
    public string salt { get; set; } = string.Empty;
}
