using LoginAndReg.Models.Entities;

namespace LoginAndReg.Services;

public interface IFileStorageService
{
    Task<UserFile> UploadFileAsync(int userId, IFormFile file, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UserFile>> GetUserFilesAsync(int userId, CancellationToken cancellationToken = default);
    Task<string> GetFileUrlAsync(string objectPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the file's S3 objects (original plus thumbnail, when
    /// present). S3 DELETE is a no-op for a missing object, so the call is
    /// safe to retry (used by the idempotent DELETE /api/models/{id},
    /// step 3c.2).
    /// </summary>
    Task DeleteFileAsync(UserFile file, CancellationToken cancellationToken = default);
}
