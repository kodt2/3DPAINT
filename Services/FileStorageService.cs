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
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".stl", ".obj", ".step", ".stp", ".glb"
    };

    private const long MaxFileSizeBytes = 500L * 1024L * 1024L;

    private readonly IMinioClient _minioClient;
    private readonly MinioOptions _options;
    private readonly AppDbContext _context;

    private readonly ILogger<FileStorageService> _logger;

    public FileStorageService(IMinioClient minioClient, IOptions<MinioOptions> options, AppDbContext appDbContext, ILogger<FileStorageService> logger)
    {
        _minioClient = minioClient;
        _options = options.Value;
        _context = appDbContext;
        _logger = logger;
    }

    public async Task<UserFile> UploadFileAsync(int userId, IFormFile file, CancellationToken cancellationToken = default)
    {
        // 1. Начало процесса
        _logger.LogInformation(">>> [UPLOAD START] User: {UserId}, FileName: {FileName}, Size: {FileSize} bytes",
            userId, file.FileName, file.Length);

        try
        {
            // 2. Валидация
            _logger.LogDebug("Validating file: {FileName}", file.FileName);
            ValidateFile(file);

            // 3. Работа с бакетом
            _logger.LogDebug("Checking if bucket '{Bucket}' exists...", _options.BucketName);
            //await EnsureBucketExistsAsync(cancellationToken);

            // 4. Подготовка путей
            var uniqueName = await GetUniqueFileNameAsync(userId, file.FileName, cancellationToken);
            var fileId = Guid.NewGuid();
            var extension = Path.GetExtension(uniqueName);
            var storagePath = $"{userId}/{fileId}{extension}";

            _logger.LogInformation(">>> [MINIO] Uploading stream to path: {StoragePath}", storagePath);

            // 5. Загрузка в MinIO
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
            sw.Stop();
            _logger.LogInformation(">>> [MINIO] Upload finished in {ElapsedMs}ms. Path: {StoragePath}", sw.ElapsedMilliseconds, storagePath);

            // 6. Генерация превью (может быть тяжелой операцией)
            _logger.LogInformation(">>> [THUMBNAIL] Creating thumbnail for {FileId}...", fileId);
            var thumbnailPath = await CreateAndUploadThumbnailAsync(userId, fileId, uniqueName, cancellationToken);
            _logger.LogDebug("Thumbnail uploaded to: {ThumbnailPath}", thumbnailPath);

            // 7. Сохранение в БД
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
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, ">>> [UPLOAD ERROR] Failed to upload file for user {UserId}. Error: {Message}", userId, ex.Message);
            throw;
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

        return await _minioClient.PresignedGetObjectAsync(args);
    }

    private static void ValidateFile(IFormFile file)
    {
        if (file.Length <= 0)
        {
            throw new InvalidOperationException("Файл пустой.");
        }

        if (file.Length > MaxFileSizeBytes)
        {
            throw new InvalidOperationException("Размер файла превышает 500 MB.");
        }

        var extension = Path.GetExtension(file.FileName);
        if (string.IsNullOrWhiteSpace(extension) || !AllowedExtensions.Contains(extension))
        {
            throw new InvalidOperationException("Поддерживаются только STL, OBJ, GLB и STEP файлы.");
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
            await _minioClient.MakeBucketAsync(new MakeBucketArgs().WithBucket(_options.BucketName), cancellationToken);
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
