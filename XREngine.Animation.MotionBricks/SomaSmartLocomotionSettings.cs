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
    public float FacingHalflifeSeconds { get; init; } = 0.15f;
    public float VelocityDirectionHalflifeSeconds { get; init; } = 0.20f;
    public float SpeedAccelerationHalflifeSeconds { get; init; } = 0.45f;
    public float SpeedDecelerationHalflifeSeconds { get; init; } = 0.30f;
    public float SpeedRegenerationFraction { get; init; } = 0.28f;
    public float DirectionRegenerationDot { get; init; } = 0.92f;
    public float MaxTurnSpeedReduction { get; init; } = 0.45f;
    public float LookaheadSeconds { get; init; } = 0.85f;
    public float PositionScale { get; init; } = 0.95f;
    public float VelocityScale { get; init; } = 0.97f;
    public float IdleLockSpeedThreshold { get; init; } = 0.05f;
    public float MinimumReplanCooldownSeconds { get; init; } = 1.0f / 30.0f;
    public int TransitionEarlyByFrames { get; init; } = 10;
    public float SpliceBlendDurationSeconds { get; init; } = 0.05f;
}
