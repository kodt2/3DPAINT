using LoginAndReg.Models.Entities;

namespace LoginAndReg.Services;

public interface IFileStorageService
{
    Task<UserFile> UploadFileAsync(int userId, IFormFile file, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UserFile>> GetUserFilesAsync(int userId, CancellationToken cancellationToken = default);
    Task<string> GetFileUrlAsync(string objectPath, CancellationToken cancellationToken = default);
}
