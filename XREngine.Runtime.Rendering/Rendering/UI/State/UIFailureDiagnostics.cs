using Stopwatch = System.Diagnostics.Stopwatch;

namespace XREngine.Rendering.UI.State;

/// <summary>
/// Reports native UI failures that withhold or cancel accelerated output. A reason starts
/// with a failure code, such as <c>UI.Canvas.ReceiptRejected</c>, followed by a colon and a
/// description. Each report increments <see cref="ReportedCount"/> and
/// <see cref="UIWorkCounter.FailuresReported"/>, and stores the reason as
/// <see cref="LastFailure"/>. A warning is logged at most once per code every two seconds,
/// so a failure that repeats each frame stays visible without flooding the log.
/// </summary>
/// <remarks>
/// After the first report of a code, a report with a constant reason does not allocate. The throttle keeps at
/// most <see cref="MaximumTrackedCodes"/> codes. A code that does not fit is logged on every
/// report.
/// </remarks>
public static class UIFailureDiagnostics
{
    /// <summary>Most failure codes that the log throttle tracks.</summary>
    public const int MaximumTrackedCodes = 256;
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(2);
    private static readonly Dictionary<string, long> LastLogTicks = new(StringComparer.Ordinal);
    private static readonly Lock Gate = new();
    private static long _reportedCount;
    private static string? _lastFailure;

    /// <summary>Gets the number of reports since process start.</summary>
    public static long ReportedCount => Interlocked.Read(ref _reportedCount);

    /// <summary>Gets the most recent reported reason, or null.</summary>
    public static string? LastFailure => Volatile.Read(ref _lastFailure);

    /// <summary>Reports a failure. Returns true when this report also wrote a log warning.</summary>
    public static bool Report(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        Interlocked.Increment(ref _reportedCount);
        Volatile.Write(ref _lastFailure, reason);
        UIRuntimeCounters.Add(UIWorkCounter.FailuresReported);
        long now = Stopwatch.GetTimestamp();
        ReadOnlySpan<char> code = CodeOf(reason);
        lock (Gate)
        {
            Dictionary<string, long>.AlternateLookup<ReadOnlySpan<char>> lookup =
                LastLogTicks.GetAlternateLookup<ReadOnlySpan<char>>();
            if (lookup.TryGetValue(code, out long last) &&
                Stopwatch.GetElapsedTime(last, now) < LogInterval)
                return false;
            if (lookup.ContainsKey(code) || LastLogTicks.Count < MaximumTrackedCodes)
                lookup[code] = now;
        }
        XREngine.Debug.UIWarning(reason);
        return true;
    }

    private static ReadOnlySpan<char> CodeOf(string reason)
    {
        int colon = reason.IndexOf(':');
        return colon > 0 ? reason.AsSpan(0, colon) : reason.AsSpan();
    }
}
