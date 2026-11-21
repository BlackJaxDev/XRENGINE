using System.Numerics;

namespace XREngine.Animation;

/// <summary>
/// Per-tick locomotion steering snapshot sampled from <see cref="AnimStateMachine"/>
/// parameters and passed to an active <see cref="IMotionBricksLocomotionDriver"/>.
/// </summary>
public readonly record struct MotionBricksSteeringInput(
    Vector2 MoveInput,
    float DesiredFacingYawRadians,
    bool StrafeMode,
    int StyleIndex,
    bool IsGrounded,
    float WalkSpeedMetersPerSecond,
    float RunSpeedMetersPerSecond,
    float ReplanIntervalSeconds,
    float InputReplanThreshold,
    float DeltaSeconds);
