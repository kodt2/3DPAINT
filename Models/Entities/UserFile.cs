namespace LoginAndReg.Models.Entities
{
    public class UserFile
    {
        public Guid id { get; set; }
        public int user_id { get; set; }
        public string file_name { get; set; } = string.Empty;
        public string storage_path { get; set; } = string.Empty;
        public string? thumbnail_path { get; set; }
        public long file_size { get; set; }
        public DateTime created_at { get; set; }

        public User user { get; set; } = null!;
    }
}
