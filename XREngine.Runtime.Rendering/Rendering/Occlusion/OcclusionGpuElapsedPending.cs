namespace XREngine.Rendering.Occlusion;

internal readonly record struct OcclusionGpuElapsedPending(
    EOcclusionGpuElapsedStage Stage,
    ulong FrameId,
    XRRenderQuery Start,
    XRRenderQuery End);
