using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Gateway.Health;

/// <summary>
/// /health/ready probe: the gateway is "ready" only when the monolith upstream
/// answers HTTP. Any response with status below 500 (or a connection-level
/// success) counts as reachable; timeouts and connection errors make it unhealthy.
/// </summary>
public sealed class UpstreamHealthCheck : IHealthCheck
{
    public const string Name = "monolith-upstream";
    public const string Tag = "upstream";
    public const string ClientName = "upstream-probe";

    private readonly HttpClient _httpClient;
    private readonly string _upstreamAddress;

    public UpstreamHealthCheck(HttpClient httpClient, string upstreamAddress)
    {
        _httpClient = httpClient;
        _upstreamAddress = upstreamAddress;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(
                new Uri(_upstreamAddress).GetLeftPart(UriPartial.Path) + "/",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if ((int)response.StatusCode < 500)
            {
                return HealthCheckResult.Healthy();
            }

            return HealthCheckResult.Unhealthy($"Upstream returned {(int)response.StatusCode}.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return HealthCheckResult.Unhealthy("Upstream is not reachable.", exception);
        }
    }
}
