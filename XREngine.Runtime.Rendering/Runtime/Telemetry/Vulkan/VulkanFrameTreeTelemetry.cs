using System;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Root-level inclusive/exclusive classification for one correlated Vulkan
/// frame tree. Stage-exclusive time is the non-overlapping sum of classified
/// stage intervals; root-exclusive time is the explicitly unattributed gap.
/// </summary>
public readonly record struct VulkanFrameTreeTelemetry(
    TimeSpan InclusiveElapsed,
    TimeSpan StageExclusiveElapsed,
    TimeSpan RootExclusiveElapsed,
    TimeSpan WorkElapsed,
    TimeSpan WaitElapsed,
    TimeSpan NativeDriverElapsed,
    TimeSpan ExternalRuntimeElapsed,
    TimeSpan DiagnosticElapsed,
    TimeSpan WorkerOverlapElapsed,
    TimeSpan RequiredOutputCriticalPathElapsed);
