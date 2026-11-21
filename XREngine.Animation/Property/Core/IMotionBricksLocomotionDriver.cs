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
}
