namespace XREngine.Rendering;

/// <summary>
/// Shared wrap-safe timestamp conversion helpers.
/// </summary>
public static class RenderQueryTimestampMath
{
    public static ulong MaskTicks(ulong ticks, uint validBits)
        => validBits switch
        {
            0u or >= 64u => ticks,
            _ => ticks & ((1ul << (int)validBits) - 1ul),
        };

    public static ulong DeltaTicks(ulong start, ulong end, uint validBits)
    {
        if (validBits == 0u || validBits >= 64u)
            return unchecked(end - start);

        ulong mask = (1ul << (int)validBits) - 1ul;
        return (end - start) & mask;
    }

    public static ulong TicksToNanoseconds(ulong ticks, double timestampPeriodNanoseconds)
    {
        if (timestampPeriodNanoseconds <= 0.0 || double.IsNaN(timestampPeriodNanoseconds))
            return 0ul;

        double nanoseconds = ticks * timestampPeriodNanoseconds;
        return nanoseconds >= ulong.MaxValue
            ? ulong.MaxValue
            : (ulong)Math.Round(nanoseconds, MidpointRounding.AwayFromZero);
    }
}
