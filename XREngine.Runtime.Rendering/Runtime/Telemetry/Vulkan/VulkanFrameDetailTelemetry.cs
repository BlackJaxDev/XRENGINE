using System;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exact backend intervals retained beside the stable lifecycle stages for diagnostics that need
/// more detail than the coarse stage model.
/// </summary>
/// <param name="WaitFrameSlot">Aggregate slot wait, including both current-slot admission and next-slot publication.</param>
/// <param name="WaitCurrentFrameSlot">Completion wait before resetting the current slot's GPU-visible arenas and transient pools.</param>
/// <param name="WaitNextFrameSlotBeforeCollect">Next-slot completion wait before releasing collect/swap publication; CPU collection already overlaps rendering.</param>
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
    TimeSpan ResetDynamicUniformRing,
    TimeSpan WaitCurrentFrameSlot,
    TimeSpan WaitNextFrameSlotBeforeCollect);
