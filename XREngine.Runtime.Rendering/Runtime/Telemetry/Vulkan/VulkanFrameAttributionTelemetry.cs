using System;

namespace XREngine.Rendering.Vulkan;

/// <summary>Coverage of frame-root wall time by the shared coarse taxonomy.</summary>
public readonly record struct VulkanFrameAttributionTelemetry(
    TimeSpan Attributed,
    TimeSpan Unattributed,
    double AttributedRatio,
    bool HasReportableGap);
