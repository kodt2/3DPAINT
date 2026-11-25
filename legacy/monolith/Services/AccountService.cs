using LoginAndReg.Models.Entities;
using LoginAndReg.Models.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace LoginAndReg.Services;

public class AccountService : IAccountService
{
    private readonly AppDbContext _context;
    private readonly IPasswordService _passwordService;

    public AccountService(AppDbContext context, IPasswordService passwordService)
    {
        _context = context;
        _passwordService = passwordService;
    }

    public async Task<User?> AuthenticateAsync(LoginViewModel model)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.login == model.Login);
        if (user is null)
        {
            return null;
        }

        var isValid = _passwordService.VerifyPassword(model.Password, user.salt, user.password_hash);
        return isValid ? user : null;
    }

    public async Task<bool> RegisterAsync(RegistrationViewModel model)
    {
        var userExists = await _context.Users.AnyAsync(u => u.login == model.Login);
        if (userExists)
        {
            return false;
        }

        var salt = _passwordService.GenerateSalt();
        var hash = _passwordService.HashPassword(model.Password, salt);

        var user = new User
        {
            login = model.Login,
            password_hash = hash,
            salt = salt
        };



        using var transaction = await _context.Database.BeginTransactionAsync();

        try
        {
            _context.Users.Add(user);
            await _context.SaveChangesAsync();

            var person = new Person
            {
                user_id = user.id,
                full_name = model.FullName,
                birth_date = DateOnly.FromDateTime(model.BirthDate)
            };

            _context.Persons.Add(person);
            await _context.SaveChangesAsync();

            await transaction.CommitAsync();
        }
        catch (Exception)
        {
            await transaction.RollbackAsync();
            throw;
        }

        return true;
    }
}
