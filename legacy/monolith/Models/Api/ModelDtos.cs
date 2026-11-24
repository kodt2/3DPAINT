namespace LoginAndReg.Models.Api;

/// <summary>
/// One model of the current user (step 3c.2, /api/models). The DTO is
/// intentionally narrow: no storage paths (internal S3 object keys), no
/// internal endpoints (e.g. storage:9000) and no owner fields — the SPA
/// needs only enough to render a card and delete the model.
/// </summary>
public sealed record ModelDto(
    Guid Id,
    string Name,
    long SizeBytes,
    DateTime CreatedAt,
    string? ThumbnailUrl);

/// <summary>
/// Single-model read (GET /api/models/{id}): the card fields plus a
/// presigned URL to the original file (for download / the future editor).
/// </summary>
public sealed record ModelDetailDto(
    Guid Id,
    string Name,
    long SizeBytes,
    DateTime CreatedAt,
    string? ThumbnailUrl,
    string DownloadUrl);

/// <summary>
/// Paginated page of the current user's models (?page=&pageSize=,
/// ARCHITECTURE.md §7.2). Newest first; the id is a deterministic
/// tiebreaker when created_at values coincide.
/// </summary>
public sealed record ModelsPageDto(
    IReadOnlyList<ModelDto> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);
