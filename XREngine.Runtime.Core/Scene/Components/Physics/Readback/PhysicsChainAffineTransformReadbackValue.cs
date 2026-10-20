using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Components;

/// <summary>
/// Packed row-major 3x4 affine transform used for bones, sockets, and full
/// transform-mirror elements. The W values contain world translation.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct PhysicsChainAffineTransformReadbackValue(
    Vector4 Row0,
    Vector4 Row1,
    Vector4 Row2)
{
    /// <summary>Restores the C# row-vector matrix from the packed column-vector rows.</summary>
    public Matrix4x4 ToMatrix4x4()
        => new(
            Row0.X, Row1.X, Row2.X, 0.0f,
            Row0.Y, Row1.Y, Row2.Y, 0.0f,
            Row0.Z, Row1.Z, Row2.Z, 0.0f,
            Row0.W, Row1.W, Row2.W, 1.0f);
}
