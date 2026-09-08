using System.Numerics;

namespace XREngine.Rendering;

/// <summary>
/// Shared camera continuity policy for frozen view producers and temporal consumers.
/// Callers compare against their own last accepted output, never a collected candidate.
/// </summary>
internal static class RenderFrameViewHistoryPolicy
{
    internal const float TranslationThreshold = 2.0f;
    internal const float RotationThresholdDegrees = 55.0f;

    internal static bool IsPoseValid(Vector3 position, Vector3 forward)
        => float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z)
        && float.IsFinite(forward.X) && float.IsFinite(forward.Y) && float.IsFinite(forward.Z)
        && float.IsFinite(forward.LengthSquared()) && forward.LengthSquared() > 1e-8f;

    internal static bool IsDiscontinuity(
        Vector3 currentPosition, Vector3 currentForward,
        Vector3 previousPosition, Vector3 previousForward,
        float translationThreshold = TranslationThreshold,
        float rotationThresholdDegrees = RotationThresholdDegrees)
    {
        // Invalid pose data must not turn into plausible motion or retained history.
        if (!IsPoseValid(currentPosition, currentForward) || !IsPoseValid(previousPosition, previousForward))
            return true;

        float threshold = Math.Max(translationThreshold, 0.0f);
        if (Vector3.DistanceSquared(currentPosition, previousPosition) > threshold * threshold)
            return true;

        float directionDot = Vector3.Dot(Vector3.Normalize(currentForward), Vector3.Normalize(previousForward));
        float angle = Math.Clamp(rotationThresholdDegrees, 0.0f, 180.0f);
        return directionDot < MathF.Cos(angle * MathF.PI / 180.0f);
    }

    internal static Vector3 GetPoseVector(in Vector4 value) => new(value.X, value.Y, value.Z);
}
