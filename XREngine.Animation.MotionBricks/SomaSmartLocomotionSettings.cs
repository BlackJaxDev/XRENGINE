namespace XREngine.Animation.MotionBricks;

/// <summary>
/// Tuning parameters for <see cref="SomaSmartLocomotionPlanner"/>, aligned with
/// NVIDIA MotionBricks' <c>LocomotionSettings</c> and SOMA presets.
/// </summary>
public sealed class SomaSmartLocomotionSettings
{
    public static SomaSmartLocomotionSettings Default { get; } = new();

    public float StickDeadzoneEnter { get; init; } = 0.08f;
    public float StickDeadzoneExit { get; init; } = 0.05f;
    public float FacingHalflifeSeconds { get; init; } = 0.16f;
    public float VelocityDirectionHalflifeSeconds { get; init; } = 0.20f;
    public float SpeedAccelerationHalflifeSeconds { get; init; } = 0.28f;
    public float SpeedDecelerationHalflifeSeconds { get; init; } = 0.26f;
    public float SpeedRegenerationFraction { get; init; } = 0.24f;
    public float DirectionRegenerationDot { get; init; } = 0.74f;
    public float MaxTurnSpeedReduction { get; init; } = 0.35f;
    public float LookaheadSeconds { get; init; } = 1.10f;
    public float PositionScale { get; init; } = 1.0f;
    public float VelocityScale { get; init; } = 1.0f;
    public float IdleLockSpeedThreshold { get; init; } = 0.05f;
    public float MinimumReplanCooldownSeconds { get; init; } = 0.045f;
    public float ModerateTurnReplanCooldownSeconds { get; init; } = 0.14f;
    public int TransitionEarlyByFrames { get; init; } = 10;
    public float SpliceBlendDurationSeconds { get; init; } = 0.11f;
    public int TrajectorySampleCount { get; init; } = 8;
    public float TrajectoryProbeRadiusMeters { get; init; } = 0.28f;
    public float TrajectoryProbeHalfHeightMeters { get; init; } = 0.85f;
}
