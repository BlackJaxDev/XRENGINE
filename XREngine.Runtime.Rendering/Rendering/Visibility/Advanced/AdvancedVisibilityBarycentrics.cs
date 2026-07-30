using System.Numerics;

namespace XREngine.Rendering;

/// <summary>
/// CPU reference for the fragment-side perspective-correct barycentric
/// reconstruction used by attribute reconstruction.
/// </summary>
public static class AdvancedVisibilityBarycentrics
{
    private const float DegenerateEpsilon = 1.0e-8f;

    public static bool TryReconstruct(
        Vector2 pixelCenter,
        Vector4 clip0,
        Vector4 clip1,
        Vector4 clip2,
        Vector2 viewportOrigin,
        Vector2 viewportSize,
        out Vector3 barycentrics)
    {
        barycentrics = default;
        if (!IsFinite(pixelCenter) ||
            !IsFinite(clip0) ||
            !IsFinite(clip1) ||
            !IsFinite(clip2) ||
            !IsFinite(viewportOrigin) ||
            !IsFinite(viewportSize) ||
            viewportSize.X <= 0.0f ||
            viewportSize.Y <= 0.0f ||
            MathF.Abs(clip0.W) <= DegenerateEpsilon ||
            MathF.Abs(clip1.W) <= DegenerateEpsilon ||
            MathF.Abs(clip2.W) <= DegenerateEpsilon)
        {
            return false;
        }

        Vector2 p0 = ClipToPixel(clip0, viewportOrigin, viewportSize);
        Vector2 p1 = ClipToPixel(clip1, viewportOrigin, viewportSize);
        Vector2 p2 = ClipToPixel(clip2, viewportOrigin, viewportSize);
        float area = Edge(p0, p1, p2);
        if (!float.IsFinite(area) ||
            MathF.Abs(area) <= DegenerateEpsilon)
        {
            return false;
        }

        Vector3 linear = new(
            Edge(p1, p2, pixelCenter) / area,
            Edge(p2, p0, pixelCenter) / area,
            Edge(p0, p1, pixelCenter) / area);
        Vector3 perspective = new(
            linear.X / clip0.W,
            linear.Y / clip1.W,
            linear.Z / clip2.W);
        float sum = perspective.X + perspective.Y + perspective.Z;
        if (!float.IsFinite(sum) ||
            MathF.Abs(sum) <= DegenerateEpsilon)
        {
            return false;
        }

        barycentrics = perspective / sum;
        return IsFinite(barycentrics);
    }

    private static Vector2 ClipToPixel(
        Vector4 clip,
        Vector2 origin,
        Vector2 size)
        => origin +
           (new Vector2(clip.X, clip.Y) / clip.W *
            0.5f +
            new Vector2(0.5f)) *
           size;

    private static float Edge(Vector2 a, Vector2 b, Vector2 p)
        => (p.X - a.X) * (b.Y - a.Y) -
           (p.Y - a.Y) * (b.X - a.X);

    private static bool IsFinite(Vector2 value)
        => float.IsFinite(value.X) &&
           float.IsFinite(value.Y);

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) &&
           float.IsFinite(value.Y) &&
           float.IsFinite(value.Z);

    private static bool IsFinite(Vector4 value)
        => float.IsFinite(value.X) &&
           float.IsFinite(value.Y) &&
           float.IsFinite(value.Z) &&
           float.IsFinite(value.W);
}
