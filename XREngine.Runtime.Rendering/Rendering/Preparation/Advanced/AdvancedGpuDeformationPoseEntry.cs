namespace XREngine.Rendering;

/// <summary>
/// Warming cache entry that deduplicates a renderer pose within one shared
/// world frame without retaining frame-local offsets as stable identity.
/// </summary>
internal readonly record struct AdvancedGpuDeformationPoseEntry(
    ulong FrameId,
    AdvancedGpuDeformationPoseSlice Slice);
