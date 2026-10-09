namespace XREngine.Rendering.Vulkan;

/// <summary>Reports completed-frame intervals in one bounded observation window.</summary>
public readonly record struct VulkanCompletedFrameIntervalTelemetry(
    long Sequence,
    long ResetCount,
    int SampleCount,
    long DroppedSampleCount,
    double P95Milliseconds,
    bool IsValid);
