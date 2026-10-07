namespace XREngine.Rendering;

/// <summary>Describes GPU ownership of one renderer's current bone set.</summary>
public readonly record struct GpuDrivenBoneCoverageSnapshot(
    long Generation,
    int UtilizedBoneCount,
    int DrivenBoneCount,
    bool HasExternalPaletteSource,
    bool IsFullyCovered);
