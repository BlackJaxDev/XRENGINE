using System;

namespace XREngine.Rendering.Compute;

/// <summary>A stable selector bucket shared by benchmark capture and runtime policy.</summary>
public readonly record struct GpuBvhSelectorBucket(
    GpuBvhCullingBackend Backend,
    uint ViewClass,
    GpuBvhVisibilityClass VisibilityClass)
{
    /// <summary>Maps raw runtime observations to a bounded calibration bucket.</summary>
    public static GpuBvhSelectorBucket From(
        GpuBvhCullingBackend backend,
        uint activeViewCount,
        float estimatedVisibleRatio)
    {
        uint viewClass = Math.Clamp(activeViewCount, 1u, 3u);
        GpuBvhVisibilityClass visibilityClass = estimatedVisibleRatio switch
        {
            < 0.25f => GpuBvhVisibilityClass.Low,
            >= 0.75f => GpuBvhVisibilityClass.High,
            _ => GpuBvhVisibilityClass.Medium,
        };
        return new(backend, viewClass, visibilityClass);
    }
}
