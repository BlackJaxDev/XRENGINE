using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Compact visibility feedback written by GPU preparation and consumed only
/// after the producing frame-slot completion value has retired.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedAnimationVisibilityFeedback(
    AdvancedGpuHandle Entity,
    ulong LastVisibleFrame,
    float ProjectedDiameter,
    float DistanceOverRadius,
    ulong ViewMask,
    EAdvancedAnimationVisibilityFlags Flags);
