using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.Rendering.Occlusion;

/// <summary>Unclamped normalized-device-coordinate footprint of an AABB.</summary>
internal readonly struct CpuOcclusionProjectionFootprint
{
    private const float MinimumClipW = 0.00001f;

    private CpuOcclusionProjectionFootprint(float minX, float minY, float maxX, float maxY)
    {
        MinX = minX;
        MinY = minY;
        MaxX = maxX;
        MaxY = maxY;
    }

    internal float MinX { get; }
    internal float MinY { get; }
    internal float MaxX { get; }
    internal float MaxY { get; }
    internal float Width => MathF.Max(0.0f, MaxX - MinX);
    internal float Height => MathF.Max(0.0f, MaxY - MinY);
    internal Vector2 Center => new((MinX + MaxX) * 0.5f, (MinY + MaxY) * 0.5f);
    internal float ViewportEdgeMargin
        => MathF.Min(
            MathF.Min(MinX + 1.0f, 1.0f - MaxX),
            MathF.Min(MinY + 1.0f, 1.0f - MaxY));
    internal bool IntersectsViewport
        => MaxX >= -1.0f && MinX <= 1.0f && MaxY >= -1.0f && MinY <= 1.0f;

    internal static bool TryProject(
        in AABB bounds,
        in Matrix4x4 viewProjection,
        out CpuOcclusionProjectionFootprint footprint)
    {
        footprint = default;
        if (!bounds.IsValid)
            return false;

        float minX = float.PositiveInfinity;
        float minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxY = float.NegativeInfinity;
        int clipWSign = 0;
        Vector3 min = bounds.Min;
        Vector3 max = bounds.Max;

        if (!Accumulate(new Vector3(min.X, min.Y, min.Z), viewProjection, ref minX, ref minY, ref maxX, ref maxY, ref clipWSign) ||
            !Accumulate(new Vector3(max.X, min.Y, min.Z), viewProjection, ref minX, ref minY, ref maxX, ref maxY, ref clipWSign) ||
            !Accumulate(new Vector3(min.X, max.Y, min.Z), viewProjection, ref minX, ref minY, ref maxX, ref maxY, ref clipWSign) ||
            !Accumulate(new Vector3(max.X, max.Y, min.Z), viewProjection, ref minX, ref minY, ref maxX, ref maxY, ref clipWSign) ||
            !Accumulate(new Vector3(min.X, min.Y, max.Z), viewProjection, ref minX, ref minY, ref maxX, ref maxY, ref clipWSign) ||
            !Accumulate(new Vector3(max.X, min.Y, max.Z), viewProjection, ref minX, ref minY, ref maxX, ref maxY, ref clipWSign) ||
            !Accumulate(new Vector3(min.X, max.Y, max.Z), viewProjection, ref minX, ref minY, ref maxX, ref maxY, ref clipWSign) ||
            !Accumulate(new Vector3(max.X, max.Y, max.Z), viewProjection, ref minX, ref minY, ref maxX, ref maxY, ref clipWSign))
        {
            return false;
        }

        footprint = new CpuOcclusionProjectionFootprint(minX, minY, maxX, maxY);
        return true;
    }

    private static bool Accumulate(
        in Vector3 point,
        in Matrix4x4 viewProjection,
        ref float minX,
        ref float minY,
        ref float maxX,
        ref float maxY,
        ref int clipWSign)
    {
        Vector4 clip = Vector4.Transform(new Vector4(point, 1.0f), viewProjection);
        if (!float.IsFinite(clip.X) || !float.IsFinite(clip.Y) || !float.IsFinite(clip.W) ||
            MathF.Abs(clip.W) <= MinimumClipW)
        {
            return false;
        }

        int sign = clip.W > 0.0f ? 1 : -1;
        if (clipWSign != 0 && clipWSign != sign)
            return false;
        clipWSign = sign;

        float inverseW = 1.0f / clip.W;
        float x = clip.X * inverseW;
        float y = clip.Y * inverseW;
        if (!float.IsFinite(x) || !float.IsFinite(y))
            return false;

        minX = MathF.Min(minX, x);
        minY = MathF.Min(minY, y);
        maxX = MathF.Max(maxX, x);
        maxY = MathF.Max(maxY, y);
        return true;
    }
}
