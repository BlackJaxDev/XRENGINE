using System.Numerics;
using XREngine.Components.Animation;

namespace XREngine.Components.Movement;

/// <summary>
/// Camera-relative locomotion steering snapshot produced by
/// <c>CharacterPawnComponent</c> when root-motion steering mode is active.
/// </summary>
public readonly record struct SteeringSnapshot(
    Vector2 DesiredWorldMoveXZ,
    float CameraYawRadians,
    bool StrafeMode,
    bool SprintHeld,
    bool IsGrounded,
    ELocomotionStyle LocomotionStyle = ELocomotionStyle.Standing,
    float VerticalVelocity = 0.0f);
