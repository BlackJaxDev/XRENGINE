namespace XREngine.Rendering;

/// <summary>
/// Operational state and history validation contract for temporal accumulation and post chain.
/// </summary>
public static class AdvancedTemporalHistoryContract
{
    public const string ReactiveMaskResourceName = "AdvancedShading.ReactiveMask";

    /// <summary>
    /// Evaluates whether the temporal history for a camera or view is currently valid.
    /// </summary>
    public static bool IsHistoryValid(AdvancedTemporalResetFlags resetFlags, uint frameIndex)
        => resetFlags == AdvancedTemporalResetFlags.None && frameIndex > 0u;
}
