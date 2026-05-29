using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// GPU skinning palette record stored as three vec4 rows.
/// Each row is dotted with vec4(position, 1) in shaders.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct SkinPaletteMatrix : IEquatable<SkinPaletteMatrix>
{
    public readonly Vector4 Row0;
    public readonly Vector4 Row1;
    public readonly Vector4 Row2;

    public static SkinPaletteMatrix Identity { get; } = FromRowVectorMatrix(Matrix4x4.Identity);

    public SkinPaletteMatrix(Vector4 row0, Vector4 row1, Vector4 row2)
    {
        Row0 = row0;
        Row1 = row1;
        Row2 = row2;
    }

    public static SkinPaletteMatrix FromRowVectorMatrix(in Matrix4x4 matrix)
        => new(
            new Vector4(matrix.M11, matrix.M21, matrix.M31, matrix.M41),
            new Vector4(matrix.M12, matrix.M22, matrix.M32, matrix.M42),
            new Vector4(matrix.M13, matrix.M23, matrix.M33, matrix.M43));

    public Matrix4x4 ToRowVectorMatrix()
        => new(
            Row0.X, Row1.X, Row2.X, 0.0f,
            Row0.Y, Row1.Y, Row2.Y, 0.0f,
            Row0.Z, Row1.Z, Row2.Z, 0.0f,
            Row0.W, Row1.W, Row2.W, 1.0f);

    public bool Equals(SkinPaletteMatrix other)
        => Row0.Equals(other.Row0)
        && Row1.Equals(other.Row1)
        && Row2.Equals(other.Row2);

    public override bool Equals(object? obj)
        => obj is SkinPaletteMatrix other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(Row0, Row1, Row2);
}
