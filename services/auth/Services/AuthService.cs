using Auth.Data;
using Auth.Entities;
using Auth.Models;
using Auth.Options;
using Auth.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Auth.Services;

/// <summary>
/// ASP.NET Identity-backed authentication (PBKDF2 password hashing — ADR 0018).
/// The monolith's SHA-256+salt scheme is deliberately NOT reused.
/// </summary>
public sealed class AuthService : IAuthService
{
    private readonly UserManager<AuthUser> _userManager;
    private readonly PasswordPolicyOptions _passwordOptions;

    public AuthService(UserManager<AuthUser> userManager, IOptions<PasswordPolicyOptions> passwordOptions)
    {
        _userManager = userManager;
        _passwordOptions = passwordOptions.Value;
    }

    public async Task<AuthUser> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        ValidatePassword(request.Password!);

        // Explicit duplicate check: a deterministic 409 (the monolith's contract)
        // rather than relying on the DB unique-constraint error to surface.
        // (UserManager exposes no CancellationToken overloads in Identity 10.)
        if (await _userManager.FindByNameAsync(request.Login!) is not null)
        {
            throw new DuplicateLoginException();
        }

        var user = new AuthUser
        {
            UserName = request.Login,
            FullName = request.FullName,
            BirthDate = request.BirthDate,
        };

        var created = await _userManager.CreateAsync(user, request.Password!);
        if (!created.Succeeded)
        {
            // Pre-validation covers the expected cases; a failure here is
            // unexpected (e.g. a duplicate that slipped past the check above).
            var message = created.Errors.FirstOrDefault()?.Description
                ?? "Не удалось создать пользователя.";
            throw new WeakPasswordException(message);
        }

        await _userManager.AddToRoleAsync(user, RoleSeeder.DefaultRole);
        return user;
    }

    public async Task<AuthUser?> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var user = await _userManager.FindByNameAsync(request.Login!);
        if (user is null)
        {
            return null;
        }

        // Lockout (step 4.2, ADR 0018): 5 failures lock the account for 15
        // minutes. A locked account returns the SAME 401 body as a wrong
        // password, so the state is not distinguishable from the response.
        if (user.LockoutEnabled && user.LockoutEnd > DateTimeOffset.UtcNow)
        {
            return null;
        }

        if (!await _userManager.CheckPasswordAsync(user, request.Password!))
        {
            // Identity's failure counter (AccessFailedAttempts) drives the
            // automatic lockout; the count is reset on a successful login.
            await _userManager.AccessFailedAsync(user);
            return null;
        }

        await _userManager.ResetAccessFailedCountAsync(user);
        return user;
    }

    public async Task<AuthUser?> GetByIdAsync(int id, CancellationToken ct)
        => await _userManager.FindByIdAsync(id.ToString());

    private void ValidatePassword(string password)
    {
        if (password.Length < _passwordOptions.MinLength)
        {
            throw new WeakPasswordException(
                $"Пароль должен быть не короче {_passwordOptions.MinLength} символов.");
        }

        if (CommonPasswords.IsCommon(password))
        {
            throw new WeakPasswordException("Пароль слишком простой (из списка частых паролей).");
        }
    }
}
