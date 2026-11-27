namespace XREngine.Animation.MotionBricks;

/// <summary>
/// Holds real 30 Hz SOMA mocap 4-frame target tokens and full idle clips loaded from
/// staged NVIDIA MotionBricks Smart Primitive JSON and BVH assets.
/// </summary>
public sealed class SomaMocapLocomotionAssetSet
{
    public SomaPoseTransform[]? IdleTarget { get; init; }
    public SomaPoseTransform[]? WalkTarget { get; init; }
    public SomaPoseTransform[]? JogTarget { get; init; }
    public SomaPoseTransform[]? RunTarget { get; init; }

    public SomaPoseTransform[]? StrafeForwardTarget { get; init; }
    public SomaPoseTransform[]? StrafeBackwardTarget { get; init; }
    public SomaPoseTransform[]? StrafeLeftTarget { get; init; }
    public SomaPoseTransform[]? StrafeRightTarget { get; init; }

    public SomaPoseTransform[]? CrouchIdleTarget { get; init; }
    public SomaPoseTransform[]? CrouchForwardTarget { get; init; }
    public SomaPoseTransform[]? CrouchBackwardTarget { get; init; }
    public SomaPoseTransform[]? CrouchLeftTarget { get; init; }
    public SomaPoseTransform[]? CrouchRightTarget { get; init; }

    public SomaMotionClip[] StandingIdleClips { get; init; } = [];
    public SomaMotionClip[] CrouchIdleClips { get; init; } = [];
    public SomaMotionClip? JumpAirborneClip { get; init; }
    public SomaMotionClip? JumpLandingClip { get; init; }

    /// <summary>
    /// True when the full 1D linear locomotion target set (<c>Idle</c>, <c>Walk</c>, <c>Jog</c>, <c>Run</c>)
    /// is populated from real mocap clips so SOMA can run with <c>has_local_poses = 1</c>.
    /// </summary>
    public bool HasLinearMocapTargets => IdleTarget is not null
        && WalkTarget is not null
        && JogTarget is not null
        && RunTarget is not null;
}
