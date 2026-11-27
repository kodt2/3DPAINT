using System.Security.Claims;
using LoginAndReg.Models.Api;
using LoginAndReg.Models.Entities;
using LoginAndReg.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LoginAndReg.Controllers
{
    /// <summary>
    /// JSON model-library endpoints for the SPA (step 3c.2). The route
    /// matches the target contract (ARCHITECTURE.md §7.2): in Phase 5 the
    /// Models Service takes over /api/models and the SPA stays unchanged
    /// (ADR 0013).
    ///
    /// Ownership (IDOR): the user filter is part of the same SQL WHERE
    /// clause as the id filter (WHERE id = @id AND user_id = @userId) — a
    /// foreign id is indistinguishable from a missing one: 404, never 403,
    /// so the API does not leak the existence of other users' models.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("api/models")]
    public class ModelsApiController : ControllerBase
    {
        /// <summary>Pagination bounds for ?page=&pageSize= (ARCHITECTURE.md §7.2).</summary>
        public const int DefaultPageSize = 20;
        public const int MinPageSize = 1;
        public const int MaxPageSize = 100;

        private readonly IFileStorageService _fileStorage;
        private readonly AppDbContext _context;
        private readonly ILogger<ModelsApiController> _logger;

        public ModelsApiController(
            IFileStorageService fileStorage,
            AppDbContext context,
            ILogger<ModelsApiController> logger)
        {
            _fileStorage = fileStorage;
            _context = context;
            _logger = logger;
        }

        /// <summary>GET /api/models?page=1&amp;pageSize=20 — the current user's models, newest first.</summary>
        [HttpGet]
        [ProducesResponseType(typeof(ModelsPageDto), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> GetMyModels(
            int? page,
            int? pageSize,
            CancellationToken cancellationToken)
        {
            var pageNumber = page ?? 1;
            var pageSizeValue = pageSize ?? DefaultPageSize;
            if (pageNumber < 1 || pageSizeValue < MinPageSize || pageSizeValue > MaxPageSize)
            {
                return Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Bad Request",
                    detail: $"page must be >= 1 and pageSize between {MinPageSize} and {MaxPageSize}.");
            }

            var owner = await GetCurrentOwnerAsync(cancellationToken);
            if (owner is null)
            {
                return Unauthorized();
            }

            var query = _context.UserFiles
                .Where(f => f.user_id == owner)
                .OrderByDescending(f => f.created_at)
                .ThenByDescending(f => f.id);

            var totalCount = await query.CountAsync(cancellationToken);
            // long + clamp: a crafted large page must not overflow the
            // int skip count; beyond the last page the result is just empty.
            var skip = Math.Min((long)(pageNumber - 1) * pageSizeValue, int.MaxValue);
            var files = await query
                .Skip((int)skip)
                .Take(pageSizeValue)
                .ToListAsync(cancellationToken);

            return Ok(new ModelsPageDto(
                await ToModelDtosAsync(files, cancellationToken),
                pageNumber,
                pageSizeValue,
                totalCount,
                (totalCount + pageSizeValue - 1) / pageSizeValue));
        }

        /// <summary>
        /// GET /api/models/{id} — one model of the current user; 404 for a
        /// foreign or missing id (no existence leak).
        /// </summary>
        [HttpGet("{id:guid}")]
        [ProducesResponseType(typeof(ModelDetailDto), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetMyModel(Guid id, CancellationToken cancellationToken)
        {
            var owner = await GetCurrentOwnerAsync(cancellationToken);
            if (owner is null)
            {
                return Unauthorized();
            }

            var file = await _context.UserFiles
                .Where(f => f.id == id && f.user_id == owner)
                .AsNoTracking()
                .FirstOrDefaultAsync(cancellationToken);
            if (file is null)
            {
                return Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Not Found",
                    detail: "Model not found.");
            }

            return Ok(new ModelDetailDto(
                file.id,
                file.file_name,
                file.file_size,
                file.created_at,
                await PresignAsync(file.thumbnail_path, cancellationToken),
                await _fileStorage.GetFileUrlAsync(file.storage_path, cancellationToken)));
        }

        /// <summary>
        /// DELETE /api/models/{id} — removes the DB record and the S3
        /// objects (original + thumbnail) of the current user's model; 404
        /// for a foreign or missing id. Repeating the DELETE is idempotent:
        /// the record is already gone, so a second call is a plain 404 (S3
        /// DELETE of a missing object is a no-op, which also makes a
        /// partially failed first attempt safely retryable).
        /// </summary>
        [HttpDelete("{id:guid}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> DeleteMyModel(Guid id, CancellationToken cancellationToken)
        {
            var owner = await GetCurrentOwnerAsync(cancellationToken);
            if (owner is null)
            {
                return Unauthorized();
            }

            // Owner check is part of the query itself: a foreign id is a 404.
            var file = await _context.UserFiles
                .Where(f => f.id == id && f.user_id == owner)
                .FirstOrDefaultAsync(cancellationToken);
            if (file is null)
            {
                return Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Not Found",
                    detail: "Model not found.");
            }

            try
            {
                // S3 first (both deletes are no-ops when the object is
                // already gone), then the DB record — a storage failure
                // leaves the record in place, so the client can retry.
                await _fileStorage.DeleteFileAsync(file, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to delete S3 objects of model {ModelId}; the DB record is kept, the delete can be retried",
                    id);
                return Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "Internal Server Error",
                    detail: "Не удалось удалить файлы в хранилище, попробуйте ещё раз.");
            }

            _context.UserFiles.Remove(file);
            await _context.SaveChangesAsync(cancellationToken);
            return NoContent();
        }

        /// <summary>
        /// The owner as a string (step 4.3a): <c>sub</c> for the Auth RS256
        /// tokens, the legacy numeric id for the HS256 monolith tokens — see
        /// <see cref="CurrentOwner"/>. Null means unauthenticated.
        /// </summary>
        private Task<string?> GetCurrentOwnerAsync(CancellationToken cancellationToken)
            => CurrentOwner.ResolveAsync(_context, User, cancellationToken);

        private async Task<IReadOnlyList<ModelDto>> ToModelDtosAsync(
            IReadOnlyList<UserFile> files,
            CancellationToken cancellationToken)
        {
            var result = new List<ModelDto>(files.Count);
            foreach (var file in files)
            {
                result.Add(new ModelDto(
                    file.id,
                    file.file_name,
                    file.file_size,
                    file.created_at,
                    await PresignAsync(file.thumbnail_path, cancellationToken)));
            }

            return result;
        }

        private async Task<string?> PresignAsync(string? objectPath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(objectPath))
            {
                return null;
            }

            return await _fileStorage.GetFileUrlAsync(objectPath, cancellationToken);
        }
    }
}
