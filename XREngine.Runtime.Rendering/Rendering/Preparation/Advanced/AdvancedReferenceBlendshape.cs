using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Weighted bind-space blendshape delta used by the diagnostic reference path.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedReferenceBlendshape(
    Vector3 PositionDelta,
    Vector3 NormalDelta,
    Vector3 TangentDelta,
    float Weight);
