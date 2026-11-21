namespace XREngine.Rendering.Commands;

/// <summary>
/// Marks one scene publication pass on the current thread. Source witnesses that
/// are expensive to recompute, such as the per-face check of a CPU-backed cube,
/// keep their result for the rest of the pass. A source change inside one pass
/// is seen by the next pass.
/// </summary>
internal static class AdvancedSourceWitnessEpoch
{
    private static long s_nextEpoch;

    [ThreadStatic]
    private static ulong t_current;

    /// <summary>Gets the epoch of the active pass on this thread, or 0 outside a pass.</summary>
    internal static ulong Current => t_current;

    /// <summary>Starts a pass on this thread and returns the previous epoch for <see cref="End"/>.</summary>
    internal static ulong Begin()
    {
        ulong previous = t_current;
        t_current = unchecked((ulong)Interlocked.Increment(ref s_nextEpoch));
        return previous;
    }

    /// <summary>Ends a pass started by <see cref="Begin"/>.</summary>
    internal static void End(ulong previous)
        => t_current = previous;
}
