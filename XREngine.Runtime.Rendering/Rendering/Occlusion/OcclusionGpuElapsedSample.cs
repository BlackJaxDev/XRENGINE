namespace XREngine.Rendering.Occlusion;

/// <summary>One delayed Hi-Z elapsed-GPU measurement and its source-frame age.</summary>
public readonly record struct OcclusionGpuElapsedSample(
    EOcclusionGpuElapsedAvailability Availability,
    ulong ElapsedNanoseconds,
    ulong SourceFrameId,
    ulong AgeFrames,
    ulong Sequence);
