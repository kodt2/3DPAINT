namespace Auth.Options;

/// <summary>
/// Password policy (ADR 0018). Enforced server-side on register; the built-in
/// common-passwords list (<c>Security.CommonPasswords</c>) is always applied on
/// top of <see cref="MinLength"/>.
/// </summary>
public sealed class PasswordPolicyOptions
{
    public const string SectionName = "Password";

    /// <summary>Minimum password length (10 — step 4.1).</summary>
    public int MinLength { get; set; } = 10;

    /// <summary>
    /// The uniform error message returned for a login failure (wrong password or
    /// unknown login). Identical for both cases so an attacker cannot tell which
    /// logins exist (ADR 0018). Matches the monolith's message verbatim so the
    /// SPA sees the same contract.
    /// </summary>
    public const string LoginFailureMessage = "Неверный логин или пароль";
}
