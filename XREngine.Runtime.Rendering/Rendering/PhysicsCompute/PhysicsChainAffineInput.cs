using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.Compute;

/// <summary>Stores an affine row-vector matrix in three GPU-aligned columns.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct PhysicsChainAffineInput
{
    public Vector4 Column0;
    public Vector4 Column1;
    public Vector4 Column2;

    public static PhysicsChainAffineInput FromMatrix(in Matrix4x4 matrix)
        => new()
        {
            Column0 = new(matrix.M11, matrix.M12, matrix.M13, matrix.M41),
            Column1 = new(matrix.M21, matrix.M22, matrix.M23, matrix.M42),
            Column2 = new(matrix.M31, matrix.M32, matrix.M33, matrix.M43),
        };

    public readonly Matrix4x4 ToMatrix()
        => new(
            Column0.X, Column0.Y, Column0.Z, 0.0f,
            Column1.X, Column1.Y, Column1.Z, 0.0f,
            Column2.X, Column2.Y, Column2.Z, 0.0f,
            Column0.W, Column1.W, Column2.W, 1.0f);

    public static bool IsFiniteAffine(in Matrix4x4 matrix)
        => matrix.M14 == 0.0f && matrix.M24 == 0.0f &&
            matrix.M34 == 0.0f && matrix.M44 == 1.0f &&
            float.IsFinite(matrix.M11) && float.IsFinite(matrix.M12) && float.IsFinite(matrix.M13) &&
            float.IsFinite(matrix.M21) && float.IsFinite(matrix.M22) && float.IsFinite(matrix.M23) &&
            float.IsFinite(matrix.M31) && float.IsFinite(matrix.M32) && float.IsFinite(matrix.M33) &&
            float.IsFinite(matrix.M41) && float.IsFinite(matrix.M42) && float.IsFinite(matrix.M43);
}
