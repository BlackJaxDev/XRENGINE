using System.Numerics;

namespace XREngine;

public readonly record struct RvcTemporalHashGridKey(int X, int Y, int Z, int NormalOctX, int NormalOctY, byte RoughnessBucket)
{
    public static RvcTemporalHashGridKey FromSurface(
        Vector3 worldPosition,
        Vector3 normal,
        float cellSizeMeters,
        byte roughnessBucket)
    {
        float inv = 1.0f / MathF.Max(cellSizeMeters, 0.0001f);
        Vector3 n = Vector3.Normalize(normal);
        float denom = MathF.Abs(n.X) + MathF.Abs(n.Y) + MathF.Abs(n.Z);
        Vector2 oct = denom > 0.0f ? new Vector2(n.X, n.Y) / denom : Vector2.Zero;
        if (n.Z < 0.0f)
            oct = new Vector2(
                (1.0f - MathF.Abs(oct.Y)) * MathF.Sign(oct.X),
                (1.0f - MathF.Abs(oct.X)) * MathF.Sign(oct.Y));

        return new(
            (int)MathF.Floor(worldPosition.X * inv),
            (int)MathF.Floor(worldPosition.Y * inv),
            (int)MathF.Floor(worldPosition.Z * inv),
            (int)MathF.Round(oct.X * 127.0f),
            (int)MathF.Round(oct.Y * 127.0f),
            roughnessBucket);
    }
}
