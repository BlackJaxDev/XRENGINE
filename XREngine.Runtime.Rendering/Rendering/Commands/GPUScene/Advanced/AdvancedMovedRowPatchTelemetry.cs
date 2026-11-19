using System.Threading;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Process counters of the moved-row patch of the Advanced unchanged-row gate: the publications
/// that patched moved rows instead of running the whole-scene plan, the rows that they patched,
/// and the publications that fell back to the whole-scene plan, by reason. The swap thread
/// writes the counters; readers take a copy. No method allocates except <see cref="CopyFallbacks"/>.
/// </summary>
public static class AdvancedMovedRowPatchTelemetry
{
    private static readonly long[] s_fallbacks = new long[(int)EAdvancedMovedRowPatchFallback.Count];
    private static long s_patches;
    private static long s_rowsPatched;

    /// <summary>Gets the number of publications that patched moved rows.</summary>
    public static long Patches => Interlocked.Read(ref s_patches);

    /// <summary>Gets the number of rows that the moved-row patch updated.</summary>
    public static long RowsPatched => Interlocked.Read(ref s_rowsPatched);

    /// <summary>Gets the number of fallbacks to the whole-scene plan for <paramref name="reason"/>.</summary>
    public static long GetFallbacks(EAdvancedMovedRowPatchFallback reason)
        => (uint)reason < (uint)s_fallbacks.Length ? Interlocked.Read(ref s_fallbacks[(int)reason]) : 0L;

    /// <summary>Returns a copy of the fallback counters, indexed by <see cref="EAdvancedMovedRowPatchFallback"/>.</summary>
    public static long[] CopyFallbacks()
    {
        long[] copy = new long[s_fallbacks.Length];
        for (int index = 0; index < copy.Length; ++index)
            copy[index] = Interlocked.Read(ref s_fallbacks[index]);
        return copy;
    }

    internal static void RecordPatch(int rows)
    {
        Interlocked.Increment(ref s_patches);
        Interlocked.Add(ref s_rowsPatched, rows);
    }

    internal static void RecordFallback(EAdvancedMovedRowPatchFallback reason)
    {
        if ((uint)reason < (uint)s_fallbacks.Length)
            Interlocked.Increment(ref s_fallbacks[(int)reason]);
    }
}
