namespace XREngine.Rendering;

/// <summary>
/// A device timestamp in both diagnostic raw ticks and public nanoseconds.
/// </summary>
public readonly record struct TimestampQueryResult(ulong RawTicks, ulong Nanoseconds);
