namespace LoginAndReg.Models.Entities
{
    /// <summary>
    /// <c>user_id</c> is the owner as a string (step 4.3a): for the RS256
    /// tokens of the Auth service it is the <c>sub</c> claim (the Auth user
    /// id), for the legacy HS256 monolith tokens it is the numeric
    /// <c>Users.id</c> kept as a string. There is no FK to <c>Users</c> —
    /// Auth users do not exist in the monolith database (ADR 0018).
    /// </summary>
    public class UserFile
    {
        public Guid id { get; set; }
        public string user_id { get; set; } = string.Empty;
        public string file_name { get; set; } = string.Empty;
        public string storage_path { get; set; } = string.Empty;
        public string? thumbnail_path { get; set; }
        public long file_size { get; set; }
        public DateTime created_at { get; set; }
    }
}
