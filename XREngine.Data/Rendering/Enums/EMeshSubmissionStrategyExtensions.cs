namespace XREngine.Data.Rendering;

public static class EMeshSubmissionStrategyExtensions
{
    public const string LegacyGpuMeshletName = "GpuMeshlet";

    /// <summary>
    /// Returns whether the strategy submits scene visibility through the GPU-driven path.
    /// GPU-driven strategies use the scene command BVH when its resources are ready.
    /// </summary>
    public static bool UsesGpuBvhCulling(this EMeshSubmissionStrategy strategy)
        => strategy != EMeshSubmissionStrategy.CpuDirect;

    public static bool IsGpuZeroReadbackStrategy(this EMeshSubmissionStrategy strategy)
        => strategy is EMeshSubmissionStrategy.GpuIndirectZeroReadback
            or EMeshSubmissionStrategy.GpuMeshletZeroReadback;

    public static bool IsAnyMeshletStrategy(this EMeshSubmissionStrategy strategy)
        => strategy is EMeshSubmissionStrategy.GpuMeshletZeroReadback
            or EMeshSubmissionStrategy.GpuMeshletInstrumented;

    public static bool IsInstrumentedMeshletStrategy(this EMeshSubmissionStrategy strategy)
        => strategy == EMeshSubmissionStrategy.GpuMeshletInstrumented;

    public static bool IsZeroReadbackMeshletStrategy(this EMeshSubmissionStrategy strategy)
        => strategy == EMeshSubmissionStrategy.GpuMeshletZeroReadback;

    public static EMeshSubmissionStrategy ToZeroReadbackMeshletStrategy(this EMeshSubmissionStrategy strategy)
        => strategy.IsAnyMeshletStrategy()
            ? EMeshSubmissionStrategy.GpuMeshletZeroReadback
            : strategy;

    public static bool TryParseMeshSubmissionStrategy(
        string? raw,
        out EMeshSubmissionStrategy strategy,
        out bool usedLegacyName)
    {
        strategy = default;
        usedLegacyName = false;

        if (string.IsNullOrWhiteSpace(raw))
            return false;

        string trimmed = raw.Trim();
        if (string.Equals(trimmed, LegacyGpuMeshletName, StringComparison.OrdinalIgnoreCase))
        {
            strategy = EMeshSubmissionStrategy.GpuMeshletZeroReadback;
            usedLegacyName = true;
            return true;
        }

        return Enum.TryParse(trimmed, ignoreCase: true, out strategy)
            && Enum.IsDefined(strategy);
    }
}
