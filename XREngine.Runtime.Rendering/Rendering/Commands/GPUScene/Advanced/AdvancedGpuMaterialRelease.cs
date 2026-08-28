namespace XREngine.Rendering.Commands;

/// <summary>Aggregated draw-owner releases for one canonical material variant.</summary>
internal readonly record struct AdvancedGpuMaterialRelease(
    AdvancedGpuHandle Material,
    uint Count);
