using System.Numerics;

namespace XREngine.Components.Animation;

/// <summary>
/// Unity-compatible HumanLimit data expressed in the joint's canonical basis.
/// </summary>
public sealed class HumanoidAvatarJointLimit
{
    public bool UseDefaultValues { get; set; } = true;
    public Vector3 CenterDegrees { get; set; }
    public Vector3 MinimumDegrees { get; set; } = new(-180.0f);
    public Vector3 MaximumDegrees { get; set; } = new(180.0f);
    public float AxisLength { get; set; }
}
