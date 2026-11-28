using Serilog.Core;
using Serilog.Events;

namespace Auth.Tests;

/// <summary>
/// A Serilog sink that captures rendered log events in memory so tests can
/// assert that sensitive values (e.g. plaintext passwords) never reach the
/// application logs (step 4.3b pre-check, ADR 0018 §12).
/// Thread-safe: the host logs from multiple request threads.
/// </summary>
public sealed class CapturingLogSink : ILogEventSink
{
    private readonly object _gate = new();
    private readonly List<string> _lines = new();

    public void Emit(LogEvent logEvent)
    {
        var line = logEvent.RenderMessage();
        lock (_gate)
        {
            _lines.Add(line);
        }
    }

    /// <summary>A snapshot of all rendered log lines captured so far.</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate)
            {
                return _lines.ToList();
            }
        }
    }
}
