using System.Numerics;

namespace XREngine.Animation;

/// <summary>
/// Allocation-free root-motion source implemented by procedural or neural motions
/// (such as <see cref="MotionBricksLocomotionMotion"/>) so state-machine root-motion
/// composition can consume them alongside <see cref="AnimationClip"/> leaves.
/// </summary>
public interface IHumanoidProceduralRootMotionSource
{
    /// <summary>
    /// Reads the canonical reference Body pose and the current evaluated Body pose
    /// in XRENGINE humanoid coordinates.
    /// </summary>
    bool TryGetBodySamples(
        out Vector3 canonicalBodyPosition,
        out Quaternion canonicalBodyRotation,
        out Vector3 currentBodyPosition,
        out Quaternion currentBodyRotation);

    /// <summary>
    /// Copies the current normalized humanoid muscle values into <paramref name="destination"/>
    /// for root-projection feet-height evaluation.
    /// </summary>
    void CopyProjectionMuscles(Span<float> destination);
}
