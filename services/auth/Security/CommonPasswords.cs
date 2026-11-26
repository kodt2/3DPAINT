namespace Auth.Security;

/// <summary>
/// Built-in list of the most common passwords (lower-case), rejected on
/// register (ADR 0018). A deliberately conservative, embedded list — it is not
/// a substitute for a full breached-password feed, which is out of scope here.
/// </summary>
public static class CommonPasswords
{
    private static readonly HashSet<string> Set = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "password1", "password123", "123456", "12345678", "123456789",
        "1234567890", "12345", "1234", "123450", "000000", "111111", "121212",
        "qwerty", "qwerty123", "qwertyuiop", "abc123", "abcd1234", "abcd12345",
        "letmein", "welcome", "welcome1", "welcome123", "admin", "admin123",
        "administrator", "iloveyou", "iloveyou1", "monkey", "dragon", "master",
        "hello", "hello123", "charlie", "donald", "shadow", "sunshine", "princess",
        "football", "baseball", "superman", "batman", "trustno1", "starwars",
        "whatever", "passw0rd", "p@ssword", "pass123", "pass1234", "passw0rd123",
        "secret", "secret123", "qwerty1", "654321", "666666", "696969", "7777777",
        "888888", "999999", "159753", "1q2w3e4r", "1qaz2wsx", "1qazxsw2", "qazwsx",
        "qaz123", "zaq12wsx", "asdfgh", "asdf1234", "zxcvbnm", "zxcvbnm1", "zxcvbn",
        "asdfghjkl", "asdf123", "123123", "112233", "123321", "12344321", "4321",
        "555555", "66666", "777777", "1234qwer", "qwer1234", "q1w2e3r4", "qwe123",
        "qweasd", "qweqwe", "1q2w3e", "123qwe", "qwe123qwe", "asdfghjkl",
        "zxcvbnmasdf", "a123456", "a123456789", "b123456", "aa123456", "a12345",
        "a1234567", "abc1234", "abc12345", "abcd123", "abcde", "abcdef", "abcdefg",
        "abcdef123", "abc123456", "abcd123456", "computer", "internet", "ninja",
        "whatever1", "whatever123", "michael", "jordan", "harley", "ranger",
        "hunter", "thomas", "robert", "soccer", "hockey", "killer", "pepper",
        "ginger", "summer", "winter", "spring", "george", "orange", "corvette",
        "mustang", "andrew", "thunder", "tigger", "purple", "cheese", "banana",
        "chicken", "coffee", "silver", "golden", "marina", "flower", "angel",
        "money", "freedom", "jordan1", "michael1", "jessica", "jennifer", "joshua",
        "joshua1", "michelle", "nicole", "amanda", "ashley", "bailey", "brianna",
        "christina", "elizabeth", "emily", "emily1", "heather", "katherine", "lauren",
        "melissa", "samantha", "sarah", "sarah1", "stephanie", "taylor", "taylor1",
        "jordan23", "maddog", "mustang1", "shadow1", "superman1", "batman1",
        "dragon1", "master1", "hello1", "hello1234", "welcome1234", "monkey123",
        "princess1", "sunshine1", "football1", "baseball1", "iloveyou123",
        "trustno1!", "trustno11", "starwars1", "charlie1", "donald1", "abc456",
        "passw0rd!", "p@ssw0rd", "letmein1", "admin1234", "root", "root123",
        "toor", "pass", "pass1", "pass12", "pass12345", "password!", "passw0rd!",
    };

    /// <summary>True when the password (case-insensitive) is in the common list.</summary>
    public static bool IsCommon(string password) => Set.Contains(password);
}
