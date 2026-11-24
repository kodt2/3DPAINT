using System.Security.Claims;
using LoginAndReg.Models.Entities;
using LoginAndReg.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LoginAndReg.Controllers;

[Authorize]
[ApiController]
[Route("api/files")]
public class UserFilesController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IFileStorageService _fileStorageService;

    public UserFilesController(AppDbContext context, IFileStorageService fileStorageService)
    {
        _context = context;
        _fileStorageService = fileStorageService;
    }

    [HttpGet]
    public async Task<IActionResult> GetMyFiles(CancellationToken cancellationToken)
    {
        var userId = await GetCurrentUserIdAsync(cancellationToken);
        if (userId is null)
        {
            return Unauthorized();
        }

        var files = await _fileStorageService.GetUserFilesAsync(userId.Value, cancellationToken);
        var response = new List<object>(files.Count);

        foreach (var file in files)
        {
            var fileUrl = await _fileStorageService.GetFileUrlAsync(file.storage_path, cancellationToken);
            string? thumbnailUrl = null;
            if (!string.IsNullOrWhiteSpace(file.thumbnail_path))
            {
                thumbnailUrl = await _fileStorageService.GetFileUrlAsync(file.thumbnail_path, cancellationToken);
            }

            response.Add(new
            {
                id = file.id,
                fileName = file.file_name,
                fileSize = file.file_size,
                createdAt = file.created_at,
                previewUrl = fileUrl,
                thumbnailUrl
            });
        }

        return Ok(response);
    }

    // No [RequestSizeLimit] here: the limit is configured once from
    // MAX_UPLOAD_BYTES (ADR 0015) and enforced by Kestrel/FormOptions
    // (body level) and by FileStorageService (file level, readable 413).
    [HttpPost("upload")]
    public async Task<IActionResult> Upload([FromForm] IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null)
        {
            return BadRequest(new { message = "Файл не передан." });
        }

        var userId = await GetCurrentUserIdAsync(cancellationToken);
        if (userId is null)
        {
            return Unauthorized();
        }

        try
        {
            var saved = await _fileStorageService.UploadFileAsync(userId.Value, file, cancellationToken);
            var fileUrl = await _fileStorageService.GetFileUrlAsync(saved.storage_path, cancellationToken);
            var thumbnailUrl = saved.thumbnail_path is null
                ? null
                : await _fileStorageService.GetFileUrlAsync(saved.thumbnail_path, cancellationToken);

            return Ok(new
            {
                id = saved.id,
                fileName = saved.file_name,
                fileSize = saved.file_size,
                createdAt = saved.created_at,
                previewUrl = fileUrl,
                thumbnailUrl
            });
        }
        catch (FileSizeLimitExceededException ex)
        {
            return Problem(
                detail: ex.Message,
                title: "File too large",
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    private async Task<int?> GetCurrentUserIdAsync(CancellationToken cancellationToken)
    {
        var login = User.FindFirstValue(ClaimTypes.Name);
        if (string.IsNullOrWhiteSpace(login))
        {
            return null;
        }

        return await _context.Users
            .Where(u => u.login == login)
            .Select(u => (int?)u.id)
            .SingleOrDefaultAsync(cancellationToken);
    }
}
