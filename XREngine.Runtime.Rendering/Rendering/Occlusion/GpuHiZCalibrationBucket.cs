using System;

namespace XREngine.Rendering.Occlusion;

/// <summary>
/// Stable workload identity shared by an offline matched benchmark and a future
/// explicit Hi-Z policy. It intentionally excludes per-frame camera state.
/// </summary>
public readonly record struct GpuHiZCalibrationBucket(
    string BackendId,
    string GpuIdentity,
    uint Width,
    uint Height,
    uint InputClass,
    int RenderPass,
    bool IsMasked,
    bool ReversedDepth,
    int ClipDepthRange,
    string SceneFingerprint)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(BackendId) &&
        !string.IsNullOrWhiteSpace(GpuIdentity) &&
        Width > 0u &&
        Height > 0u &&
        !string.IsNullOrWhiteSpace(SceneFingerprint);

    public void Validate()
    {
        if (!IsValid)
            throw new ArgumentException("A GPU Hi-Z calibration bucket requires backend, GPU, extent, and scene identities.");
    }
}
