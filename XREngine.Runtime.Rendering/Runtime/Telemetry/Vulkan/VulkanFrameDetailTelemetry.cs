using System;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exact backend intervals retained beside the stable lifecycle stages for diagnostics that need
/// more detail than the coarse stage model.
/// </summary>
public readonly record struct VulkanFrameDetailTelemetry(
    TimeSpan WaitFrameSlot,
    TimeSpan AcquireImage,
    TimeSpan RecordCommandBuffer,
    TimeSpan SnapshotImGuiOverlay,
    TimeSpan RecordSceneCommandBuffer,
    TimeSpan RecordImGuiOverlay,
    TimeSpan RecordDynamicUiTextOverlay,
    TimeSpan SubmitQueue,
    TimeSpan TrimStaging,
    TimeSpan PresentQueue,
    TimeSpan SampleTimingQueries,
    TimeSpan DrainRetiredResources,
    TimeSpan AcquireBridgeSubmit,
    TimeSpan WaitSwapchainImage,
    TimeSpan ResetDynamicUniformRing);
