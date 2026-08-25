using System.Numerics;

namespace XREngine.Components.Animation;

/// <summary>
/// Extract-only model-root pose projected from the current humanoid Body Transform.
/// Position uses XRENGINE handedness and engine length units, is aligned to the
/// evaluator's canonical Body-yaw frame, and is relative to that canonical sample.
/// Rotation is the canonical-relative yaw in the same frame. This value is not a
/// scene-root local or world transform and never moves a scene node by itself.
/// </summary>
public readonly record struct HumanoidProjectedRootPose(
    Vector3 Position,
    Quaternion Rotation,
    EHumanoidProjectedRootChannels Channels)
{
    public static HumanoidProjectedRootPose Identity { get; } = new(
        Vector3.Zero,
        Quaternion.Identity,
        EHumanoidProjectedRootChannels.None);
}
