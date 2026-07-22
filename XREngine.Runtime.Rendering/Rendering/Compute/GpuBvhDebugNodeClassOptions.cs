using System.Numerics;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Supplies a GPU-resident per-node classification buffer to a BVH debug overlay.
/// Zero means unclassified; one and two select the corresponding colors and visibility bits.
/// </summary>
public readonly record struct GpuBvhDebugNodeClassOptions(
    XRDataBuffer Buffer,
    GpuBvhDebugNodeClassMode Mode,
    Vector4 ClassOneColor,
    Vector4 ClassTwoColor,
    uint VisibleClassMask);
