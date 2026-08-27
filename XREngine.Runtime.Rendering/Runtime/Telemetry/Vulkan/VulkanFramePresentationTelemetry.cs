using System;

namespace XREngine.Rendering.Vulkan;

/// <summary>Per-frame presentation, limiter, and native WSI intervals.</summary>
public readonly record struct VulkanFramePresentationTelemetry(
    TimeSpan ActualPresentInterval,
    TimeSpan LimiterSleep,
    TimeSpan LimiterSpin,
    TimeSpan QueueSubmitAdmission,
    TimeSpan NativeQueueSubmit,
    TimeSpan QueuePresentAdmission,
    TimeSpan NativeQueuePresent,
    int FramesAhead,
    uint AcquireUnavailableCount,
    bool PresentDispatched,
    bool PresentationAccepted);
