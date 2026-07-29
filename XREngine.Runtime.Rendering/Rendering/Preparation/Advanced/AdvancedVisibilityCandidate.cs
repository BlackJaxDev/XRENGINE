using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// GPU-readable early visibility candidate keyed by a stable draw handle.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 80)]
public readonly record struct AdvancedVisibilityCandidate(
    AdvancedGpuHandle Draw,
    Vector4 BoundsSphere,
    Vector4 BoundsMin,
    Vector4 BoundsMax,
    ulong ViewMask,
    uint BvhLeaf,
    EAdvancedVisibilityPreparationFlags Flags);
