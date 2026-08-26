using System.Numerics;

namespace XREngine.Components.Animation;

/// <summary>
/// Endpoint-to-start correction for the root-relative Body pose. The residual is
/// evaluated after root projection and is intentionally independent of temporal
/// root-loop accumulation.
/// </summary>
public readonly record struct HumanoidLoopPoseCorrection(
    Vector3 EndpointTranslation,
    Quaternion EndpointRotation,
    float Phase)
{
    public static HumanoidLoopPoseCorrection Identity { get; } = new(
        Vector3.Zero,
        Quaternion.Identity,
        0.0f);

    public HumanoidLoopPoseCorrection AtPhase(float phase)
        => new(EndpointTranslation, EndpointRotation, Math.Clamp(phase, 0.0f, 1.0f));
}
