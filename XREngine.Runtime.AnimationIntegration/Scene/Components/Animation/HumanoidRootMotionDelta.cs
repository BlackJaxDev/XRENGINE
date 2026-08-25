using System.Numerics;

namespace XREngine.Components.Animation;

/// <summary>
/// Temporal change between two consecutive projected humanoid root poses.
/// Translation is current minus previous in the projected pose's canonical Body-yaw
/// frame; rotation is inverse(previous) times current in that same frame. This type
/// does not prescribe scene-transform multiplication or locomotion application order.
/// The first sample after an owner or lifecycle change has no valid delta channels.
/// This remains diagnostic until a locomotion policy atomically removes the same components from Hips.
/// </summary>
public readonly record struct HumanoidRootMotionDelta(
    Vector3 Translation,
    Quaternion Rotation,
    EHumanoidProjectedRootChannels Channels)
{
    public static HumanoidRootMotionDelta Identity { get; } = new(
        Vector3.Zero,
        Quaternion.Identity,
        EHumanoidProjectedRootChannels.None);
}
