using System.Numerics;

namespace XREngine.Rendering;

/// <summary>
/// CPU reference for dense per-eye velocity and reactive validity.
/// </summary>
public static class AdvancedReconstructionTemporalContract
{
    public static AdvancedReconstructionMotion Resolve(
        Vector4 currentUnjitteredClip,
        Vector4 previousUnjitteredClip,
        EAdvancedVelocityValidityReason validityReason,
        bool maskedEdge)
    {
        bool valid =
            validityReason == EAdvancedVelocityValidityReason.Valid &&
            IsFinite(currentUnjitteredClip) &&
            IsFinite(previousUnjitteredClip) &&
            MathF.Abs(currentUnjitteredClip.W) >
                AdvancedSurfaceContract.MinimumClipW &&
            MathF.Abs(previousUnjitteredClip.W) >
                AdvancedSurfaceContract.MinimumClipW;
        bool reactive =
            maskedEdge ||
            validityReason != EAdvancedVelocityValidityReason.Valid;
        if (!valid)
            return new AdvancedReconstructionMotion(Vector2.Zero, false, reactive);

        Vector2 current =
            new(currentUnjitteredClip.X, currentUnjitteredClip.Y);
        current /= currentUnjitteredClip.W;
        Vector2 previous =
            new(previousUnjitteredClip.X, previousUnjitteredClip.Y);
        previous /= previousUnjitteredClip.W;
        Vector2 motion = Vector2.Clamp(
            current - previous,
            new Vector2(-AdvancedSurfaceContract.MaximumMotionNdc),
            new Vector2(AdvancedSurfaceContract.MaximumMotionNdc));
        return new AdvancedReconstructionMotion(motion, true, reactive);
    }

    private static bool IsFinite(Vector4 value)
        => float.IsFinite(value.X) &&
           float.IsFinite(value.Y) &&
           float.IsFinite(value.Z) &&
           float.IsFinite(value.W);
}
