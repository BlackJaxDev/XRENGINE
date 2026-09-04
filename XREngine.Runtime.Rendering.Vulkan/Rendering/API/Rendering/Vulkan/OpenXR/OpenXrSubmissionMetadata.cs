namespace XREngine.Rendering.Vulkan;

/// <summary>Immutable OpenXR lifecycle identity captured before command recording starts.</summary>
internal readonly record struct OpenXrSubmissionMetadata(
    ulong FrameId,
    long PredictedDisplayTime);
