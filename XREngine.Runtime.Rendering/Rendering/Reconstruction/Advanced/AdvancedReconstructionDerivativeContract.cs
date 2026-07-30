using System.Numerics;

namespace XREngine.Rendering;

/// <summary>
/// Numeric reference for analytical-gradient diagnostics and conservative LOD.
/// </summary>
public static class AdvancedReconstructionDerivativeContract
{
    public static AdvancedReconstructionDerivativeResult ResolveSelectedMip(
        Vector2 uvDx,
        Vector2 uvDy,
        Vector2 textureSize,
        uint mipCount,
        bool derivativesValid)
    {
        float maximumMip = Math.Max(mipCount, 1u) - 1u;
        if (!derivativesValid ||
            !IsFinite(uvDx) ||
            !IsFinite(uvDy) ||
            !IsFinite(textureSize) ||
            textureSize.X <= 0.0f ||
            textureSize.Y <= 0.0f)
        {
            return new(maximumMip, true);
        }

        Vector2 scaledDx = uvDx * textureSize;
        Vector2 scaledDy = uvDy * textureSize;
        float footprintSquared = Math.Max(
            scaledDx.LengthSquared(),
            scaledDy.LengthSquared());
        if (!float.IsFinite(footprintSquared) ||
            footprintSquared < 0.0f)
        {
            return new(maximumMip, true);
        }
        if (footprintSquared == 0.0f)
            return new(0.0f, false);

        float mip = Math.Clamp(
            0.5f * MathF.Log2(footprintSquared),
            0.0f,
            maximumMip);
        return new(mip, false);
    }

    public static float CalculateError(
        Vector2 analyticalDx,
        Vector2 analyticalDy,
        Vector2 neighborDx,
        Vector2 neighborDy)
        => Math.Max(
            Vector2.Distance(analyticalDx, neighborDx),
            Vector2.Distance(analyticalDy, neighborDy));

    public static bool MayCompareNeighbor(
        in AdvancedVisibilityPayloadWords center,
        in AdvancedVisibilityPayloadWords neighbor)
        => center.IsValid && center == neighbor;

    private static bool IsFinite(Vector2 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y);
}
