using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Components;

/// <summary>
/// Packed row-major 3x4 affine transform used for bones, sockets, and full
/// transform-mirror elements.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct PhysicsChainAffineTransformReadbackValue(
    Vector4 Row0,
    Vector4 Row1,
    Vector4 Row2);
