using LoginAndReg.Models.Entities;
using LoginAndReg.Models.ViewModels;

namespace LoginAndReg.Services;

public interface IAccountService
{
    Task<User?> AuthenticateAsync(LoginViewModel model);
    Task<bool> RegisterAsync(RegistrationViewModel model);
}
