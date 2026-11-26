using System.Buffers.Binary;
using System.Text;
using LoginAndReg.Models.Entities;
using LoginAndReg.Models.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;

using static Microsoft.Extensions.Logging.ILogger;

namespace LoginAndReg.Services;

public class FileStorageService : IFileStorageService
{
    private readonly IMinioClient _minioClient;
    private readonly MinioOptions _options;
    private readonly long _maxUploadBytes;
    private readonly IReadOnlyList<string> _allowedExtensions;
    private readonly AppDbContext _context;

    private readonly ILogger<FileStorageService> _logger;

    // Lazily created client for signing presigned URLs against the public
    // endpoint (TD-3). All data operations keep using the internal client.
    private IMinioClient? _presignClient;

    public FileStorageService(
        IMinioClient minioClient,
        IOptions<MinioOptions> options,
        IOptions<UploadOptions> uploadOptions,
        AppDbContext appDbContext,
        ILogger<FileStorageService> logger)
    {
        _minioClient = minioClient;
        _options = options.Value;
        _maxUploadBytes = uploadOptions.Value.MaxBytes;
        // Single source of truth shared with the public /api/config (step 4.0):
        // the SPA and the server validate against the same extension list.
        _allowedExtensions = uploadOptions.Value.AllowedExtensions;
        _context = appDbContext;
        _logger = logger;
    }

    public async Task<UserFile> UploadFileAsync(int userId, IFormFile file, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(">>> [UPLOAD START] User: {UserId}, FileName: {FileName}, Size: {FileSize} bytes",
            userId, file.FileName, file.Length);

        // S3 paths already written; used to clean up orphans when the upload
        // does not finish (client abort, thumbnail or DB failure) — an upload
        // must not leave S3 objects without a DB record (step 3c.4).
        string? uploadedPath = null;
        string? uploadedThumbnailPath = null;

        try
        {
            _logger.LogDebug("Validating file: {FileName}", file.FileName);
            ValidateFile(file);

            _logger.LogDebug("Checking if bucket '{Bucket}' exists...", _options.BucketName);
            await EnsureBucketExistsAsync(cancellationToken);

            var uniqueName = await GetUniqueFileNameAsync(userId, file.FileName, cancellationToken);
            var fileId = Guid.NewGuid();
            var extension = Path.GetExtension(uniqueName);
            var storagePath = $"{userId}/{fileId}{extension}";

            _logger.LogInformation(">>> [MINIO] Uploading stream to path: {StoragePath}", storagePath);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await using (var stream = file.OpenReadStream())
            {
                var putObjectArgs = new PutObjectArgs()
                    .WithBucket(_options.BucketName)
                    .WithObject(storagePath)
                    .WithStreamData(stream)
                    .WithObjectSize(file.Length)
                    .WithContentType(file.ContentType ?? "application/octet-stream");

                await _minioClient.PutObjectAsync(putObjectArgs, cancellationToken);
            }

            // From here the object exists in S3: any failure before the DB
            // record is saved must delete it (see the catch blocks).
            uploadedPath = storagePath;
            sw.Stop();
            _logger.LogInformation(">>> [MINIO] Upload finished in {ElapsedMs}ms. Path: {StoragePath}", sw.ElapsedMilliseconds, storagePath);

            _logger.LogInformation(">>> [THUMBNAIL] Creating thumbnail for {FileId}...", fileId);
            var thumbnailPath = await CreateAndUploadThumbnailAsync(userId, fileId, uniqueName, cancellationToken);
            _logger.LogDebug("Thumbnail uploaded to: {ThumbnailPath}", thumbnailPath);
            uploadedThumbnailPath = thumbnailPath;

            var userFile = new UserFile
            {
                id = fileId,
                user_id = userId,
                file_name = uniqueName,
                storage_path = storagePath,
                thumbnail_path = thumbnailPath,
                file_size = file.Length,
                created_at = DateTime.UtcNow
            };

            _logger.LogDebug("Saving metadata to Database for file {FileId}...", fileId);
            _context.UserFiles.Add(userFile);
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(">>> [UPLOAD COMPLETE] Successfully saved file {FileId} for user {UserId}", fileId, userId);
            return userFile;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(">>> [UPLOAD CANCELLED] User {UserId} cancelled the upload or connection was closed.", userId);
            await CleanupOrphanObjectsAsync(uploadedPath, uploadedThumbnailPath);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, ">>> [UPLOAD ERROR] Failed to upload file for user {UserId}. Error: {Message}", userId, ex.Message);
            await CleanupOrphanObjectsAsync(uploadedPath, uploadedThumbnailPath);
            throw;
        }
    }

    /// <summary>
    /// Best-effort deletion of S3 objects written by an upload that did not
    /// finish (no DB record). Runs only when at least one object was
    /// written; a failed delete is logged, not thrown.
    /// </summary>
    private async Task CleanupOrphanObjectsAsync(string? objectPath, string? thumbnailPath)
    {
        foreach (var path in new[] { objectPath, thumbnailPath })
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                await _minioClient.RemoveObjectAsync(
                    new RemoveObjectArgs().WithBucket(_options.BucketName).WithObject(path),
                    CancellationToken.None);
                _logger.LogInformation(">>> [CLEANUP] Removed orphan S3 object: {Path}", path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, ">>> [CLEANUP] Failed to remove orphan S3 object: {Path}", path);
            }
        }
    }

    public async Task<IReadOnlyList<UserFile>> GetUserFilesAsync(int userId, CancellationToken cancellationToken = default)
    {
        return await _context.UserFiles
            .Where(x => x.user_id == userId)
            .OrderByDescending(x => x.created_at)
            .ToListAsync(cancellationToken);
    }

    public async Task<string> GetFileUrlAsync(string objectPath, CancellationToken cancellationToken = default)
    {
        var expiry = _options.PresignedUrlExpiryMinutes * 60;
        var args = new PresignedGetObjectArgs()
            .WithBucket(_options.BucketName)
            .WithObject(objectPath)
            .WithExpiry(expiry);

        // Sign with the public endpoint so the browser can resolve and hit the URL
        // directly; the Host inside the signature must match what the browser requests.
        return await GetPresignClient().PresignedGetObjectAsync(args);
    }

    public async Task DeleteFileAsync(UserFile file, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            ">>> [S3 DELETE] Model {ModelId}: original {StoragePath}, thumbnail {ThumbnailPath}",
            file.id,
            file.storage_path,
            file.thumbnail_path ?? "<none>");

        await _minioClient.RemoveObjectAsync(
            new RemoveObjectArgs()
                .WithBucket(_options.BucketName)
                .WithObject(file.storage_path),
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(file.thumbnail_path))
        {
            await _minioClient.RemoveObjectAsync(
                new RemoveObjectArgs()
                    .WithBucket(_options.BucketName)
                    .WithObject(file.thumbnail_path),
                cancellationToken);
        }
    }

    private IMinioClient GetPresignClient()
    {
        if (_presignClient is not null)
        {
            return _presignClient;
        }

        if (string.IsNullOrWhiteSpace(_options.PublicEndpoint))
        {
            _logger.LogWarning(
                "S3 public endpoint is not configured; presigned URLs use the internal endpoint {Endpoint} " +
                "and may be unreachable from the browser (TD-3)",
                _options.Endpoint);
            return _minioClient;
        }

        // The Minio SDK expects "host:port" without a scheme; the configured value
        // is a full URL (that is what the browser opens), so strip the scheme.
        var publicUri = new Uri(_options.PublicEndpoint);
        var endpoint = $"{publicUri.Host}:{publicUri.Port}";
        var client = new MinioClient()
            .WithEndpoint(endpoint)
            .WithCredentials(_options.AccessKey, _options.SecretKey)
            .WithSSL(publicUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            .WithHttpClient(new HttpClient(new HttpClientHandler()) { DefaultRequestVersion = System.Net.HttpVersion.Version11 });

        if (!string.IsNullOrWhiteSpace(_options.Region))
        {
            client = client.WithRegion(_options.Region);
        }

        _presignClient = client.Build();
        return _presignClient;
    }

    private void ValidateFile(IFormFile file)
    {
        if (file.Length <= 0)
        {
            throw new InvalidOperationException("Файл пустой.");
        }

        // Single limit shared with the Gateway (MAX_UPLOAD_BYTES, ADR 0015);
        // 413 with a human-readable message (files far above the limit die
        // earlier, at the body level, with a bare server response).
        if (file.Length > _maxUploadBytes)
        {
            throw new FileSizeLimitExceededException(_maxUploadBytes);
        }

        var extension = Path.GetExtension(file.FileName);
        if (string.IsNullOrWhiteSpace(extension)
            || !_allowedExtensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Поддерживаются только файлы: {string.Join(", ", _allowedExtensions)}.");
        }

        // The extension whitelist is not enough: a renamed corrupt file would
        // pollute the library and break the viewer. GLB containers carry a
        // self-describing header — verify it.
        if (string.Equals(extension, ".glb", StringComparison.OrdinalIgnoreCase))
        {
            ValidateGlbFile(file);
        }
    }

    private static void ValidateGlbFile(IFormFile file)
    {
        if (file.Length < 12)
        {
            throw new InvalidGlbFileException("Файл повреждён: GLB-файл слишком мал, чтобы содержать заголовок.");
        }

        Span<byte> header = stackalloc byte[12];
        using var stream = file.OpenReadStream();
        var read = 0;
        while (read < 12)
        {
            var n = stream.Read(header[read..]);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        if (read < 12)
        {
            throw new InvalidGlbFileException("Файл повреждён: GLB-файл слишком мал, чтобы содержать заголовок.");
        }

        if (header[0] != 'g' || header[1] != 'l' || header[2] != 'T' || header[3] != 'F')
        {
            throw new InvalidGlbFileException("Файл не является GLB: заголовок не соответствует контейнеру glTF.");
        }

        var version = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(4, 4));
        if (version != 2)
        {
            throw new InvalidGlbFileException($"Файл не является GLB: версия glTF {version} не поддерживается.");
        }

        var totalLength = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(8, 4));
        if (totalLength < 12)
        {
            throw new InvalidGlbFileException("Файл повреждён: длина в заголовке GLB некорректна.");
        }

        if (totalLength > file.Length)
        {
            throw new InvalidGlbFileException("Файл повреждён: длина в заголовке GLB больше размера файла.");
        }
    }

    private async Task EnsureBucketExistsAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(">>> [DEBUG] Checking bucket name: '{Bucket}'", _options.BucketName);
        var exists = await _minioClient.BucketExistsAsync(new BucketExistsArgs().WithBucket(_options.BucketName), cancellationToken);

        if (string.IsNullOrWhiteSpace(_options.BucketName))
        {
            throw new Exception("Имя бакета пустое! Проверьте конфигурацию.");
        }

        if (!exists)
        {
            try
            {
                await _minioClient.MakeBucketAsync(new MakeBucketArgs().WithBucket(_options.BucketName), cancellationToken);
            }
            catch (Exception ex) when (ex.Message.Contains("Bucket already owned by you", StringComparison.Ordinal))
            {
                // A concurrent request created the bucket between the check
                // and the create (fresh stand, parallel first uploads). The
                // bucket exists and is ours — nothing to do.
                _logger.LogInformation("Bucket '{Bucket}' was created concurrently; continuing.", _options.BucketName);
            }
        }
    }

    private async Task<string> GetUniqueFileNameAsync(int userId, string originalName, CancellationToken cancellationToken)
    {
        var baseName = Path.GetFileNameWithoutExtension(originalName);
        var extension = Path.GetExtension(originalName);
        var candidate = Path.GetFileName(originalName);
        var suffix = 1;

        while (await _context.UserFiles.AnyAsync(x => x.user_id == userId && x.file_name == candidate, cancellationToken))
        {
            candidate = $"{baseName} ({suffix++}){extension}";
        }

        return candidate;
    }

    private async Task<string> CreateAndUploadThumbnailAsync(int userId, Guid fileId, string fileName, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant();
        var label = fileName.Length > 24 ? fileName[..24] + "…" : fileName;

        var svg = $"""
            <svg xmlns='http://www.w3.org/2000/svg' width='512' height='320'>
              <defs>
                <linearGradient id='g' x1='0%' y1='0%' x2='100%' y2='100%'>
                  <stop offset='0%' stop-color='#171726'/>
                  <stop offset='100%' stop-color='#1f0e32'/>
                </linearGradient>
              </defs>
              <rect width='100%' height='100%' fill='url(#g)'/>
              <rect x='24' y='24' width='464' height='272' rx='24' fill='none' stroke='#00F0FF' stroke-width='3' stroke-opacity='0.6'/>
              <text x='48' y='130' fill='#ffffff' font-size='52' font-family='Inter, Arial, sans-serif'>{extension}</text>
              <text x='48' y='190' fill='#9ca4bc' font-size='24' font-family='Inter, Arial, sans-serif'>{System.Security.SecurityElement.Escape(label)}</text>
            </svg>
            """;

        var thumbnailPath = $"{userId}/thumbnails/{fileId}.svg";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(svg));

        var args = new PutObjectArgs()
            .WithBucket(_options.BucketName)
            .WithObject(thumbnailPath)
            .WithStreamData(stream)
            .WithObjectSize(stream.Length)
            .WithContentType("image/svg+xml");

        await _minioClient.PutObjectAsync(args, cancellationToken);

        return thumbnailPath;
    }
}
