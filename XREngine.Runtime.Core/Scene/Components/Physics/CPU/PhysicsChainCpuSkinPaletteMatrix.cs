using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Components;

/// <summary>Affine skin matrix stored as the renderer's three vec4 rows.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct PhysicsChainCpuSkinPaletteMatrix(Vector4 Row0, Vector4 Row1, Vector4 Row2)
{
    public static PhysicsChainCpuSkinPaletteMatrix FromRowVectorMatrix(in Matrix4x4 matrix)
        => new(
            new Vector4(matrix.M11, matrix.M21, matrix.M31, matrix.M41),
            new Vector4(matrix.M12, matrix.M22, matrix.M32, matrix.M42),
            new Vector4(matrix.M13, matrix.M23, matrix.M33, matrix.M43));
}
