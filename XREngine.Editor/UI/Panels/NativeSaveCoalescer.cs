using System.Diagnostics;

namespace XREngine.Editor;

/// <summary>
/// Tracks one pending save until requests stop for a settle interval.
/// Timestamps use <see cref="Stopwatch.GetTimestamp"/> units.
/// </summary>
internal sealed class NativeSaveCoalescer(TimeSpan settleTime)
{
    private readonly long _settleTicks = (long)(settleTime.TotalSeconds * Stopwatch.Frequency);
    private long _lastRequest;

    /// <summary>Gets whether a request waits for a save.</summary>
    public bool IsPending { get; private set; }

    /// <summary>Records a change and restarts the settle interval.</summary>
    public void Request(long timestamp)
    {
        IsPending = true;
        _lastRequest = timestamp;
    }

    /// <summary>Takes the pending request when the settle interval passed after the last request.</summary>
    public bool TryTakeSettled(long timestamp)
    {
        if (!IsPending || timestamp - _lastRequest < _settleTicks)
            return false;
        IsPending = false;
        return true;
    }

    /// <summary>Takes the pending request immediately.</summary>
    public bool TryTakePending()
    {
        if (!IsPending)
            return false;
        IsPending = false;
        return true;
    }
}
