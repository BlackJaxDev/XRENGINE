using System.Numerics;

namespace XREngine.Animation;

/// <summary>
/// Probes static world geometry to snap future MotionBricks trajectory points to terrain height
/// and clamp or slide them along upcoming walls and obstacles before the capsule collides.
/// </summary>
public interface IMotionBricksTrajectoryProber
{
    /// <summary>
    /// Advances a ground trajectory step from <paramref name="fromGround"/> by
    /// <paramref name="desiredStepXZ"/>, checking for obstacles along the step and projecting
    /// the resolved XZ point onto the supporting ground surface.
    /// </summary>
    bool ProbeTrajectoryStep(
        Vector3 fromGround,
        Vector3 desiredStepXZ,
        float capsuleRadius,
        float capsuleHalfHeight,
        out Vector3 resolvedGround,
        out Vector3 groundNormal,
        out bool hitObstacle,
        out Vector3 obstacleNormal);

    /// <summary>
    /// Queries the ground elevation beneath <paramref name="groundPosition"/>.
    /// </summary>
    bool ProbeGroundHeight(
        Vector3 groundPosition,
        float probeUpOffset,
        float probeDownDistance,
        out float groundY,
        out Vector3 groundNormal);
}
