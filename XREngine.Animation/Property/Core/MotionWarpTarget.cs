using System.Numerics;

namespace XREngine.Animation;

/// <summary>
/// Runtime world-space alignment target consumed by <see cref="MotionWarpingMotion"/>
/// for vaulting, mantling, climbing, sitting, or prop-interaction traversal clips.
/// </summary>
public readonly record struct MotionWarpTarget(
    string TargetName,
    Vector3 Position,
    Quaternion Rotation,
    bool HasTarget = true)
{
    public static MotionWarpTarget Empty(string targetName = "Default")
        => new(targetName, Vector3.Zero, Quaternion.Identity, HasTarget: false);
}
