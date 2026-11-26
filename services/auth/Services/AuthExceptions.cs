namespace Auth.Services;

/// <summary>The login is already taken (register) → HTTP 409.</summary>
public sealed class DuplicateLoginException : Exception
{
    public DuplicateLoginException()
        : base("Логин уже занят.")
    {
    }
}

/// <summary>The password fails the policy (too short / too common) → HTTP 400.</summary>
public sealed class WeakPasswordException : Exception
{
    public WeakPasswordException(string message)
        : base(message)
    {
    }
}
