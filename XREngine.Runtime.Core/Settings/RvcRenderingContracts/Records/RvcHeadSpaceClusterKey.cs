using System.Numerics;

namespace XREngine;

public readonly record struct RvcHeadSpaceClusterKey(int X, int Y, int Z)
{
    public static RvcHeadSpaceClusterKey FromWorldPosition(
        Vector3 worldPosition,
        Vector3 cameraRelativeOrigin,
        float cellSizeMeters)
    {
        float inv = 1.0f / MathF.Max(cellSizeMeters, 0.0001f);
        Vector3 relative = worldPosition - cameraRelativeOrigin;
        return new(
            (int)MathF.Floor(relative.X * inv),
            (int)MathF.Floor(relative.Y * inv),
            (int)MathF.Floor(relative.Z * inv));
    }
}
