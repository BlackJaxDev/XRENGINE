using System.Numerics;

namespace XREngine.Components.Animation;

/// <summary>
/// Captures Unity's Hips-parent pose relative to the Animator root. Mecanim
/// allocates Body and projected-root channels in this calibrated frame rather
/// than in the imported skeleton parent's runtime frame.
/// </summary>
public sealed class UnityHumanoidRootAllocationFrame
{
    public Vector3 HipsParentPositionInAnimatorRoot { get; set; }
    public Quaternion HipsParentRotationInAnimatorRoot { get; set; } = Quaternion.Identity;
    public Vector3 HipsParentScaleInAnimatorRoot { get; set; } = Vector3.One;
}
