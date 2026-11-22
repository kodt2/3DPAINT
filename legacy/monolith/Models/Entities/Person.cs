namespace LoginAndReg.Models.Entities;

public class Person
{
    public int id { get; set; }
    public int user_id {  get; set; }
    public string full_name { get; set; } = string.Empty;
    public DateOnly birth_date { get; set; }
}
