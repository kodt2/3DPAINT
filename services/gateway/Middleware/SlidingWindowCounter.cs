using System.Collections.Concurrent;

namespace Gateway.Middleware;

/// <summary>
/// In-memory sliding-window counter: at most the given number of acquisitions
/// per key within <see cref="Window"/>. Not durable — a gateway restart resets
/// the counters (acceptable for brute-force damping). Public because it is a
/// dependency of the public <see cref="AuthRateLimitMiddleware"/>.
/// </summary>
public sealed class SlidingWindowCounter
{
    private sealed class ClientWindow
    {
        public object Sync { get; } = new();

        public List<long> Timestamps { get; } = [];
    }

    /// <summary>
    /// When the per-client dictionary grows past this, empty (fully expired)
    /// windows are removed to keep memory bounded.
    /// </summary>
    private const int SweepThreshold = 4096;

    private readonly ConcurrentDictionary<string, ClientWindow> _clients = new();
    private readonly Func<long> _nowTicks;

    public SlidingWindowCounter(TimeSpan window, Func<long>? nowTicks = null)
    {
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentException("The window must be positive.", nameof(window));
        }

        Window = window;
        // Environment.TickCount64 is in MILLISECONDS despite the name; the
        // counter arithmetic (cutoff = now - Window.Ticks) is in TimeSpan
        // ticks (100 ns), so scale the real clock up. Mixing the units made
        // the "60 s window" last ~7 days (or latch forever on hosts with
        // under ~7 days of uptime, when the cutoff goes negative).
        _nowTicks = nowTicks ?? (() => Environment.TickCount64 * TimeSpan.TicksPerMillisecond);
    }

    public TimeSpan Window { get; }

    /// <summary>
    /// Records an acquisition for <paramref name="key"/> and reports whether it
    /// was accepted. On rejection <paramref name="retryAfterSeconds"/> is the
    /// number of seconds until the oldest timestamp in the window expires.
    /// </summary>
    public bool TryAcquire(string key, int limit, out long retryAfterSeconds)
    {
        var now = _nowTicks();
        var cutoff = now - Window.Ticks;

        if (_clients.Count > SweepThreshold)
        {
            SweepExpired(cutoff);
        }

        var client = _clients.GetOrAdd(key, _ => new ClientWindow());

        lock (client.Sync)
        {
            TrimExpired(client, cutoff);

            if (client.Timestamps.Count >= limit)
            {
                var untilOldestExpires = client.Timestamps[0] + Window.Ticks - now;
                retryAfterSeconds = (long)Math.Ceiling(untilOldestExpires / (double)TimeSpan.TicksPerSecond);
                if (retryAfterSeconds < 1)
                {
                    retryAfterSeconds = 1;
                }

                return false;
            }

            client.Timestamps.Add(now);
            retryAfterSeconds = 0;
            return true;
        }
    }

    private void SweepExpired(long cutoff)
    {
        foreach (var (key, client) in _clients)
        {
            lock (client.Sync)
            {
                TrimExpired(client, cutoff);
            }

            if (client.Timestamps.Count == 0)
            {
                _clients.TryRemove(key, out _);
            }
        }
    }

    private static void TrimExpired(ClientWindow client, long cutoff)
    {
        // Lists are short (bounded by the limit), so a linear trim is fine.
        while (client.Timestamps.Count > 0 && client.Timestamps[0] <= cutoff)
        {
            client.Timestamps.RemoveAt(0);
        }
    }
}
