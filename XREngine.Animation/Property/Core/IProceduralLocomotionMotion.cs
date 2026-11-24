using System.Numerics;

namespace XREngine.Animation;

/// <summary>
/// Runtime contract implemented by procedural or neural locomotion motions
/// (such as <see cref="MotionBricksLocomotionMotion"/>, motion matching, or IK stride)
/// so character root-motion bridges can bind world alignment, root-motion scale,
/// physics trajectory probers, and collision reconciliation without casting to one concrete motion.
/// </summary>
public interface IProceduralLocomotionMotion : IHumanoidProceduralRootMotionSource
{
    /// <summary>
    /// Opaque runtime driver instance token that changes whenever the motion creates
    /// or replaces its backing runtime driver. Bridges use this token to detect
    /// late-initialized drivers with zero allocations.
    /// </summary>
    object? RuntimeDriverToken { get; }

    /// <summary>
    /// Applies avatar facing yaw, animated root-motion scale, and optional physics trajectory
    /// prober settings to the motion and its current or future runtime driver.
    /// </summary>
    void ConfigureLocomotionRuntime(
        float initialWorldYawRadians,
        float worldRootMotionScale,
        IMotionBricksTrajectoryProber? trajectoryProber);

    /// <summary>
    /// Clears any bound physics trajectory prober when the bridge detaches from the animator.
    /// </summary>
    void DetachTrajectoryProber();

    /// <summary>
    /// Feeds resolved physics capsule position, velocity, and wall-block state back into
    /// the motion so walls clamp procedural root drift.
    /// </summary>
    void ApplyPhysicsRootReconciliation(
        Vector3 resolvedFootPosition,
        Vector3 actualPlanarVelocity,
        Vector3 wallNormal,
        bool isBlocked);

    /// <summary>
    /// Reads the canonical reference body rotation for the active locomotion epoch.
    /// </summary>
    bool TryGetCanonicalBodyRotation(out Quaternion canonicalBodyRotation);

    /// <summary>
    /// Reads the current absolute world-space body rotation produced by the motion.
    /// </summary>
    bool TryGetCurrentBodyRotation(out Quaternion currentBodyRotation);
}
