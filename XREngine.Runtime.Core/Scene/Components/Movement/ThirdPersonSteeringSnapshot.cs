using System.Numerics;

namespace XREngine.Components.Movement;

/// <summary>
/// Camera-relative third-person locomotion steering snapshot produced by
/// <c>CharacterPawnComponent</c> when root-motion steering mode is active.
/// </summary>
public readonly record struct ThirdPersonSteeringSnapshot(
    Vector2 DesiredWorldMoveXZ,
    float CameraYawRadians,
    bool StrafeMode,
    bool SprintHeld,
    bool IsGrounded);
