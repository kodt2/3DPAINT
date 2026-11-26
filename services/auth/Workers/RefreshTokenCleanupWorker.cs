using Auth.Options;
using Auth.Services;
using Microsoft.Extensions.Options;
using Serilog;

namespace Auth.Workers;

/// <summary>
/// Hourly background cleanup of dead refresh-token rows (revoked or expired)
/// in bounded batches (step 4.2, ADR 0018). The table is append-heavy — every
/// rotation and reuse revocation adds rows — so the dead rows are deleted in
/// the background instead of at request time. A failed tick is logged and
/// retried on the next interval; the service itself never crashes on a DB
/// blip.
/// </summary>
public sealed class RefreshTokenCleanupWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly RefreshTokenOptions _options;

    public RefreshTokenCleanupWorker(
        IServiceProvider services,
        IOptions<RefreshTokenOptions> options)
    {
        _services = services;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(_options.CleanupIntervalHours));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunCleanupAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task RunCleanupAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var refresh = scope.ServiceProvider.GetRequiredService<RefreshTokenService>();
            var removed = await refresh.CleanupExpiredAndRevokedAsync(null, ct);
            if (removed > 0)
            {
                Log.Information("Refresh token cleanup removed {Count} expired or revoked rows", removed);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Refresh token cleanup failed (will retry on the next tick)");
        }
    }
}
