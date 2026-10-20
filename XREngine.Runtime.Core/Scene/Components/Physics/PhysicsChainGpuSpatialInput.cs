using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.Components;

/// <summary>Captures conservative particle reach and authored stretch with GPU inputs.</summary>
public readonly record struct PhysicsChainGpuSpatialInput(
    AABB RootReachBounds,
    AABB SeedParticleBounds,
    float MaximumBasisStretch,
    bool IsValid)
{
    /// <summary>Bounds affine stretch, including shear, without matrix decomposition.</summary>
    public static float MaximumLinearStretch(in Matrix4x4 matrix)
    {
        float rowNorm = MathF.Max(
            MathF.Abs(matrix.M11) + MathF.Abs(matrix.M12) + MathF.Abs(matrix.M13),
            MathF.Max(MathF.Abs(matrix.M21) + MathF.Abs(matrix.M22) + MathF.Abs(matrix.M23),
                MathF.Abs(matrix.M31) + MathF.Abs(matrix.M32) + MathF.Abs(matrix.M33)));
        float columnNorm = MathF.Max(
            MathF.Abs(matrix.M11) + MathF.Abs(matrix.M21) + MathF.Abs(matrix.M31),
            MathF.Max(MathF.Abs(matrix.M12) + MathF.Abs(matrix.M22) + MathF.Abs(matrix.M32),
                MathF.Abs(matrix.M13) + MathF.Abs(matrix.M23) + MathF.Abs(matrix.M33)));
        return MathF.Sqrt(rowNorm * columnNorm);
    }
}
