using System.Net;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Gateway.Health;

/// <summary>
/// /health/ready probe: the gateway is "ready" only when the monolith's
/// /api/health endpoint answers 200. This is stricter than the previous
/// "any response below 500" check (step 4.0): the monolith root (/) returns
/// 404 since the monolith is API-only (step 3d.2), so a status-agnostic probe
/// would mark the gateway ready even when the API surface is broken (the
/// exact 3d.2 regression where /api/** returned 404). Probing /api/health and
/// requiring exactly 200 makes /health/ready actually reflect whether the API
/// the gateway fronts is serving.
/// </summary>
public sealed class UpstreamHealthCheck : IHealthCheck
{
    public const string Name = "monolith-upstream";
    public const string Tag = "upstream";
    public const string ClientName = "upstream-probe";

    /// <summary>
    /// The monolith endpoint probed for readiness; it must return 200 for the
    /// upstream to be considered healthy.
    /// </summary>
    public const string ProbePath = "/api/health";

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
        var probeUrl = BuildProbeUrl();
        try
        {
            using var response = await _httpClient.GetAsync(
                probeUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                return HealthCheckResult.Healthy();
            }

            return HealthCheckResult.Unhealthy($"Upstream {ProbePath} returned {(int)response.StatusCode}.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return HealthCheckResult.Unhealthy("Upstream is not reachable.", exception);
        }
    }

    /// <summary>
    /// Joins the upstream base address (which may carry a base path) with the
    /// probe path without producing a double slash. <c>GetLeftPart(Path)</c>
    /// normalizes a bare <c>http://host:port</c> to a trailing <c>/</c>, so the
    /// slash is trimmed before appending the absolute probe path.
    /// </summary>
    private string BuildProbeUrl()
        => new Uri(_upstreamAddress).GetLeftPart(UriPartial.Path).TrimEnd('/') + ProbePath;
}
