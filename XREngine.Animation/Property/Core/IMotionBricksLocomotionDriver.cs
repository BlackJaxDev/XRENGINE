using System.Numerics;

namespace XREngine.Animation;

/// <summary>
/// Runtime evaluation driver bound to one <see cref="MotionBricksLocomotionMotion"/> instance.
/// Implemented in <c>XREngine.Animation.MotionBricks</c> or test harnesses so
/// <c>XREngine.Animation</c> stays free of TensorRT and native inference dependencies.
/// </summary>
public interface IMotionBricksLocomotionDriver : IDisposable
{
    /// <summary>
    /// True when the backing inference runtime or generator can produce valid poses.
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// Human-readable diagnostic status or failure reason surfaced to
    /// <c>AnimStateMachineComponent</c>.
    /// </summary>
    string DiagnosticStatus { get; }

    /// <summary>
    /// Called when the owning <see cref="AnimState"/> becomes active.
    /// </summary>
    void OnStateEntered();

    /// <summary>
    /// Called when the owning <see cref="AnimState"/> finishes exiting.
    /// </summary>
    void OnStateExited();

    /// <summary>
    /// Resets playback clocks and root continuity back to the initial locomotion epoch.
    /// </summary>
    void ResetPlayback();

    /// <summary>
    /// Advances locomotion playback by <see cref="MotionBricksSteeringInput.DeltaSeconds"/>
    /// and writes the interpolated humanoid muscles and Body pose into <paramref name="destination"/>.
    /// </summary>
    void AdvanceAndEvaluate(in MotionBricksSteeringInput input, MotionBricksEvaluatedPose destination);

    /// <summary>
    /// Feeds resolved physics capsule position and wall-blocking feedback back into the
    /// locomotion runtime so animation root context stays anchored to physical collisions.
    /// </summary>
    void ApplyPhysicsRootReconciliation(
        Vector3 actualFootPosition,
        Vector3 actualPlanarVelocity,
        Vector3 wallNormal,
        bool isBlocked)
    {
    }

    /// <summary>
    /// Seeds the four 30 Hz locomotion context frames from the live humanoid pose history
    /// when entering a <see cref="MotionBricksLocomotionMotion"/> state from a traditional
    /// animation clip or blend tree.
    /// </summary>
    void SeedContextFromHumanoidHistory(ReadOnlySpan<HumanoidPoseHistoryFrame> fourContextFrames)
    {
    }

    /// <summary>
    /// Aligns the locomotion runtime's initial world-space facing yaw (radians, where 0 faces +Z)
    /// to match the mounted avatar's actual world orientation before playback starts.
    /// </summary>
    void SetInitialFacingYaw(float facingYawRadians)
    {
    }

    /// <summary>
    /// Binds an optional terrain and obstacle prober used to project future ground trajectory
    /// points onto static world geometry before dispatching SOMA replans.
    /// </summary>
    void SetTrajectoryProber(IMotionBricksTrajectoryProber? prober)
    {
    }

    /// <summary>
    /// Sets the humanoid root-motion world scale factor applied downstream by
    /// <c>HumanoidComponent.EstimateAnimatedMotionScale()</c> so trajectory projection,
    /// debug skeletons, and physics reconciliation stay matched 1:1 with world meters.
    /// </summary>
    void SetWorldRootMotionScale(float worldRootMotionScale)
    {
    }

    /// <summary>
    /// Copies the latest projected ground trajectory dots and current/future SOMA skeleton joints
    /// into a caller-owned <paramref name="destination"/> buffer without allocating.
    /// </summary>
    bool TryCopyDebugSnapshot(MotionBricksDebugSnapshot destination)
        => false;
}
