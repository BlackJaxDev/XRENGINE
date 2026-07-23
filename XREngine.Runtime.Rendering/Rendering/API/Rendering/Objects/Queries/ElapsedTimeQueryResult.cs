namespace XREngine.Rendering;

/// <summary>
/// A portable elapsed-time result produced from two timestamp points.
/// </summary>
public readonly record struct ElapsedTimeQueryResult(
    ulong StartRawTicks,
    ulong EndRawTicks,
    ulong Nanoseconds);
