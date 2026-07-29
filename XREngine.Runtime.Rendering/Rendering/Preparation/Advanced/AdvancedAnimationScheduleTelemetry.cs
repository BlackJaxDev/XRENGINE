using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Delayed-inspection row for one render-pose scheduling decision.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedAnimationScheduleTelemetry(
    AdvancedGpuHandle Entity,
    AdvancedAnimationScheduleDecision Decision);
