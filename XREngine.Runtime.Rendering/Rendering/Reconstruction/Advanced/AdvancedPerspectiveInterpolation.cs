using System.Numerics;

namespace XREngine.Rendering;

/// <summary>
/// CPU numeric reference for the shader's analytical interpolation path.
/// </summary>
public static class AdvancedPerspectiveInterpolation
{
    public static bool TryReconstruct(
        Vector2 pixelCenter,
        Vector4 clip0,
        Vector4 clip1,
        Vector4 clip2,
        Vector2 viewportOrigin,
        Vector2 viewportSize,
        out AdvancedBarycentricDerivatives result)
    {
        result = default;
        if (!IsFinite(pixelCenter) ||
            !IsFinite(clip0) ||
            !IsFinite(clip1) ||
            !IsFinite(clip2) ||
            !IsFinite(viewportOrigin) ||
            !IsFinite(viewportSize) ||
            viewportSize.X <= 0.0f ||
            viewportSize.Y <= 0.0f ||
            MathF.Abs(clip0.W) <= AdvancedSurfaceContract.MinimumClipW ||
            MathF.Abs(clip1.W) <= AdvancedSurfaceContract.MinimumClipW ||
            MathF.Abs(clip2.W) <= AdvancedSurfaceContract.MinimumClipW)
        {
            return false;
        }

        Vector2 p0 = ClipToPixel(clip0, viewportOrigin, viewportSize);
        Vector2 p1 = ClipToPixel(clip1, viewportOrigin, viewportSize);
        Vector2 p2 = ClipToPixel(clip2, viewportOrigin, viewportSize);
        float denominator =
            (p1.Y - p2.Y) * (p0.X - p2.X) +
            (p2.X - p1.X) * (p0.Y - p2.Y);
        if (!float.IsFinite(denominator) ||
            MathF.Abs(denominator) <=
                AdvancedSurfaceContract.DegenerateTriangleAreaPixels)
        {
            return false;
        }

        Vector3 linear = new(
            ((p1.Y - p2.Y) * (pixelCenter.X - p2.X) +
             (p2.X - p1.X) * (pixelCenter.Y - p2.Y)) / denominator,
            ((p2.Y - p0.Y) * (pixelCenter.X - p2.X) +
             (p0.X - p2.X) * (pixelCenter.Y - p2.Y)) / denominator,
            0.0f);
        linear.Z = 1.0f - linear.X - linear.Y;
        Vector3 linearDx = new(
            (p1.Y - p2.Y) / denominator,
            (p2.Y - p0.Y) / denominator,
            (p0.Y - p1.Y) / denominator);
        Vector3 linearDy = new(
            (p2.X - p1.X) / denominator,
            (p0.X - p2.X) / denominator,
            (p1.X - p0.X) / denominator);
        Vector3 reciprocalW = new(
            1.0f / clip0.W,
            1.0f / clip1.W,
            1.0f / clip2.W);
        Vector3 weighted = linear * reciprocalW;
        Vector3 weightedDx = linearDx * reciprocalW;
        Vector3 weightedDy = linearDy * reciprocalW;
        float sum = weighted.X + weighted.Y + weighted.Z;
        float sumDx = weightedDx.X + weightedDx.Y + weightedDx.Z;
        float sumDy = weightedDy.X + weightedDy.Y + weightedDy.Z;
        if (!float.IsFinite(sum) ||
            MathF.Abs(sum) <= AdvancedSurfaceContract.MinimumClipW)
        {
            return false;
        }

        float inverseSum = 1.0f / sum;
        Vector3 weights = weighted * inverseSum;
        Vector3 dx =
            (weightedDx * sum - weighted * sumDx) *
            (inverseSum * inverseSum);
        Vector3 dy =
            (weightedDy * sum - weighted * sumDy) *
            (inverseSum * inverseSum);
        if (!IsFinite(weights) || !IsFinite(dx) || !IsFinite(dy))
            return false;

        result = new AdvancedBarycentricDerivatives(weights, dx, dy);
        return true;
    }

    public static Vector2 Interpolate(
        Vector2 value0,
        Vector2 value1,
        Vector2 value2,
        Vector3 weights,
        bool flatQualified = false)
        => flatQualified
            ? value0
            : value0 * weights.X +
              value1 * weights.Y +
              value2 * weights.Z;

    public static Vector3 Interpolate(
        Vector3 value0,
        Vector3 value1,
        Vector3 value2,
        Vector3 weights,
        bool flatQualified = false)
        => flatQualified
            ? value0
            : value0 * weights.X +
              value1 * weights.Y +
              value2 * weights.Z;

    public static Vector4 Interpolate(
        Vector4 value0,
        Vector4 value1,
        Vector4 value2,
        Vector3 weights,
        bool flatQualified = false)
        => flatQualified
            ? value0
            : value0 * weights.X +
              value1 * weights.Y +
              value2 * weights.Z;

    public static Vector2 Gradient(
        Vector2 value0,
        Vector2 value1,
        Vector2 value2,
        Vector3 weightDerivative)
        => value0 * weightDerivative.X +
           value1 * weightDerivative.Y +
           value2 * weightDerivative.Z;

    private static Vector2 ClipToPixel(
        Vector4 clip,
        Vector2 origin,
        Vector2 size)
        => origin +
           (new Vector2(clip.X, clip.Y) / clip.W *
            0.5f +
            new Vector2(0.5f)) *
           size;

    private static bool IsFinite(Vector2 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y);

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
