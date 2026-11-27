using System.Security.Cryptography;
using System.Text.Json.Serialization;
using LoginAndReg.Models.Options;
using Microsoft.IdentityModel.Tokens;

namespace LoginAndReg.Security;

/// <summary>
/// Outcome of <see cref="JwksKeyProvider.EnsureLoadedAsync"/> for one
/// <c>kid</c>.
/// </summary>
public enum JwksLoadResult
{
    /// <summary>The key is in the cache (before or after a fetch).</summary>
    Found,

    /// <summary>
    /// The key is unknown and no fetch happened (throttled, or another
    /// request is fetching). The caller should fail authentication.
    /// </summary>
    Throttled,

    /// <summary>The JWKS fetch failed (network/HTTP/parse); the old cache is kept.</summary>
    FetchFailed,

    /// <summary>The JWKS fetched fine but does not contain this <c>kid</c>.</summary>
    NotInJwks,
}

/// <summary>
/// Lazy, cached JWKS key source for the Auth service (step 4.3a, ADR 0018).
///
/// - No fetch at startup: the first RS256 token triggers the first load, so
///   the monolith boots and answers /api/health even when Auth is down.
/// - The key set is kept in memory and replaced wholesale on every fetch
///   (the JWKS document lists the current AND the previous key — rotation).
/// - Unknown-<c>kid</c> refreshes are throttled to once per
///   <see cref="AuthJwksOptions.RefreshThrottleSeconds"/> (default 30 s):
///   an attacker cannot use forged <c>kid</c>s to hammer the Auth service.
/// - A failed fetch keeps the previous cache and degrades to 401 on the
///   protected endpoints (never 500); the warning is logged at most once
///   per throttle window.
/// </summary>
public sealed class JwksKeyProvider
{
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(5);

    private readonly AuthJwksOptions _options;
    private readonly ILogger<JwksKeyProvider> _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _fetchLock = new(1, 1);

    private Dictionary<string, RsaSecurityKey> _keys = new(StringComparer.Ordinal);
    private DateTime _lastAttemptUtc = DateTime.MinValue;

    public JwksKeyProvider(AuthJwksOptions options, ILogger<JwksKeyProvider> logger)
    {
        _options = options;
        _logger = logger;
        // Own client: fetches are rare (throttled) and the lifetime is the
        // app lifetime, so no IHttpClientFactory churn is needed here.
        _http = new HttpClient { Timeout = FetchTimeout };
    }

    /// <summary>
    /// Synchronous cache lookup for the JWT <c>IssuerSigningKeyResolver</c>
    /// (which has no async hook). The cache is always filled before the
    /// validator runs — see <see cref="EnsureLoadedAsync"/> in
    /// OnMessageReceived.
    /// </summary>
    public SecurityKey? Resolve(string? kid)
    {
        if (string.IsNullOrEmpty(kid))
        {
            return null;
        }

        return _keys.TryGetValue(kid, out var key) ? key : null;
    }

    /// <summary>
    /// Makes sure <paramref name="kid"/> is in the cache, fetching the JWKS
    /// document when it is unknown (throttled). Never throws: every failure
    /// mode maps to a <see cref="JwksLoadResult"/> so the caller turns it
    /// into a clean 401.
    /// </summary>
    public async Task<JwksLoadResult> EnsureLoadedAsync(string kid, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(kid))
        {
            return JwksLoadResult.NotInJwks;
        }

        if (TryGet(kid, out _))
        {
            return JwksLoadResult.Found;
        }

        // One fetch at a time; a concurrent fetch in progress is reported as
        // Throttled (it will refresh the cache; the next request wins).
        if (!_fetchLock.Wait(TimeSpan.FromSeconds(1), cancellationToken))
        {
            return TryGet(kid, out _) ? JwksLoadResult.Found : JwksLoadResult.Throttled;
        }

        try
        {
            var elapsed = DateTime.UtcNow - _lastAttemptUtc;
            if (elapsed < TimeSpan.FromSeconds(_options.RefreshThrottleSeconds))
            {
                return TryGet(kid, out _) ? JwksLoadResult.Found : JwksLoadResult.Throttled;
            }

            _lastAttemptUtc = DateTime.UtcNow;

            try
            {
                var document = await FetchDocumentAsync(_http, _options.Url, cancellationToken).ConfigureAwait(false);
                var next = new Dictionary<string, RsaSecurityKey>(StringComparer.Ordinal);
                foreach (var key in document.Keys)
                {
                    if (
                        string.Equals(key.Kty, "RSA", StringComparison.Ordinal)
                        && !string.IsNullOrEmpty(key.Kid)
                        && TryCreateRsaKey(key, out var rsaKey))
                    {
                        next[key.Kid] = rsaKey;
                    }
                }

                if (next.Count == 0)
                {
                    throw new InvalidDataException("The JWKS document contains no usable RSA keys.");
                }

                _keys = next;
                return TryGet(kid, out _) ? JwksLoadResult.Found : JwksLoadResult.NotInJwks;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to fetch the JWKS document from {Url}; RS256 tokens cannot be validated until the next attempt (throttled to once per {Seconds}s)",
                    _options.Url,
                    _options.RefreshThrottleSeconds);
                return JwksLoadResult.FetchFailed;
            }
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    private static async Task<JwksDocument> FetchDocumentAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        var document = await http.GetFromJsonAsync<JwksDocument>(url, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The JWKS endpoint returned an empty document.");
        if (document.Keys is null)
        {
            throw new InvalidDataException("The JWKS document has no 'keys' array.");
        }

        return document;
    }

    private bool TryGet(string kid, out RsaSecurityKey key)
        => _keys.TryGetValue(kid, out key!);

    private static bool TryCreateRsaKey(Jwk key, out RsaSecurityKey rsaKey)
    {
        rsaKey = null!;
        if (string.IsNullOrEmpty(key.N) || string.IsNullOrEmpty(key.E) || string.IsNullOrEmpty(key.Kid))
        {
            return false;
        }

        try
        {
            var parameters = new RSAParameters
            {
                Modulus = Base64UrlDecode(key.N!),
                Exponent = Base64UrlDecode(key.E!),
            };

            var rsa = RSA.Create();
            rsa.ImportParameters(parameters);
            rsaKey = new RsaSecurityKey(rsa) { KeyId = key.Kid };
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
            default: break;
        }
        return Convert.FromBase64String(padded);
    }

    private sealed record JwksDocument(List<Jwk> Keys);

    private sealed record Jwk
    {
        [JsonPropertyName("kty")]
        public string? Kty { get; init; }

        [JsonPropertyName("use")]
        public string? Use { get; init; }

        [JsonPropertyName("alg")]
        public string? Alg { get; init; }

        [JsonPropertyName("kid")]
        public string? Kid { get; init; }

        [JsonPropertyName("n")]
        public string? N { get; init; }

        [JsonPropertyName("e")]
        public string? E { get; init; }
    }
}
