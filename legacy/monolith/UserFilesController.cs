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
        var owner = await GetCurrentOwnerAsync(cancellationToken);
        if (owner is null)
        {
            return Unauthorized();
        }

        var files = await _fileStorageService.GetUserFilesAsync(owner, cancellationToken);
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

        var owner = await GetCurrentOwnerAsync(cancellationToken);
        if (owner is null)
        {
            return Unauthorized();
        }

        try
        {
            var saved = await _fileStorageService.UploadFileAsync(owner, file, cancellationToken);
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
        catch (InvalidGlbFileException ex)
        {
            // ProblemDetails on purpose (step 3c.4 contract).
            return Problem(
                detail: ex.Message,
                title: "Invalid GLB file",
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// The owner as a string (step 4.3a) — see
    /// <see cref="CurrentOwner"/>. Null means unauthenticated.
    /// </summary>
    private Task<string?> GetCurrentOwnerAsync(CancellationToken cancellationToken)
        => CurrentOwner.ResolveAsync(_context, User, cancellationToken);
}
