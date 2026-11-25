using System.Net;

namespace Gateway.Middleware;

/// <summary>
/// Resolves the per-client key used by <see cref="AuthRateLimitMiddleware"/>.
/// </summary>
public interface IClientKeyResolver
{
    /// <summary>
    /// Returns a stable per-client identifier for rate limiting.
    /// </summary>
    string Resolve(HttpContext context);
}

/// <summary>
/// Default client key: the socket peer address, unless the connection comes
/// from a trusted proxy (KNOWN_PROXIES / KNOWN_NETWORKS). For trusted
/// connections the client address is taken from X-Forwarded-For: the chain is
/// walked right-to-left (the rightmost entry was added by the closest proxy)
/// and the first non-trusted entry is the client. This mirrors what
/// X-Forwarded-For is supposed to carry; untrusted senders can forge XFF, so
/// it is only honored when the connection itself comes from a trusted proxy.
/// </summary>
public sealed class TrustedProxyClientKeyResolver : IClientKeyResolver
{
    private readonly IReadOnlyList<IPAddress> _knownProxies;
    private readonly IReadOnlyList<IPNetwork> _knownNetworks;

    public TrustedProxyClientKeyResolver(IReadOnlyCollection<IPAddress> knownProxies, IReadOnlyCollection<IPNetwork> knownNetworks)
    {
        _knownProxies = knownProxies.Select(ToCanonical).Distinct().ToList();
        _knownNetworks = knownNetworks.ToList();
    }

    /// <summary>
    /// Builds the resolver from the KNOWN_PROXIES / KNOWN_NETWORKS settings
    /// (comma-separated IPs / CIDRs). Both empty (default): nothing is trusted.
    /// </summary>
    public static TrustedProxyClientKeyResolver FromConfiguration(IConfiguration configuration)
    {
        var proxies = ParseCommaSeparated(configuration["KNOWN_PROXIES"], entry =>
            {
                if (!IPAddress.TryParse(entry, out var ip))
                {
                    throw InvalidProxyValue("KNOWN_PROXIES", entry);
                }

                return ip;
            });

        var networks = ParseCommaSeparated(configuration["KNOWN_NETWORKS"], entry =>
        {
            try
            {
                return IPNetwork.Parse(entry);
            }
            catch (FormatException)
            {
                throw InvalidProxyValue("KNOWN_NETWORKS", entry);
            }
        });

        return new TrustedProxyClientKeyResolver(proxies, networks);
    }

    private static List<T> ParseCommaSeparated<T>(string? raw, Func<string, T> parse)
        => string.IsNullOrWhiteSpace(raw)
            ? new List<T>()
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(parse).ToList();

    private static InvalidOperationException InvalidProxyValue(string setting, string entry)
        => new($"{setting} contains an unparseable value: '{entry}' (expected an IP address or CIDR network).");

    public string Resolve(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null)
        {
            // No socket address (e.g. in-memory test servers): a single shared bucket.
            return "unknown";
        }

        var canonical = ToCanonical(remote);
        if (!IsKnownProxy(canonical))
        {
            // The connection is not from a trusted proxy: XFF is forgeable and ignored.
            return canonical.ToString();
        }

        var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
        if (string.IsNullOrWhiteSpace(forwarded))
        {
            // Trusted proxy did not add XFF: the peer (the proxy) is the best key we have.
            return canonical.ToString();
        }

        var entries = forwarded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Rightmost first: skip proxies we trust, the first untrusted entry is the client.
        for (var i = entries.Length - 1; i >= 0; i--)
        {
            if (IPAddress.TryParse(entries[i], out var ip) && !IsKnownProxy(ToCanonical(ip)))
            {
                return ToCanonical(ip).ToString();
            }
        }

        // Every entry is a trusted proxy (or unparseable): fall back to the leftmost
        // entry if it is an address, otherwise to the peer.
        return entries.Length > 0 && IPAddress.TryParse(entries[0], out var leftmost)
            ? ToCanonical(leftmost).ToString()
            : canonical.ToString();
    }

    private bool IsKnownProxy(IPAddress address)
        => _knownProxies.Contains(address) || _knownNetworks.Any(network => network.Contains(address));

    /// <summary>
    /// Kestrel on Linux reports IPv4 peers as IPv4-mapped IPv6 addresses
    /// (::ffff:a.b.c.d). The canonical form is plain IPv4, so that
    /// <see cref="IPAddress"/> equality matches the configured values and the
    /// rate-limit keys of the same client are identical regardless of the form.
    /// </summary>
    private static IPAddress ToCanonical(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
