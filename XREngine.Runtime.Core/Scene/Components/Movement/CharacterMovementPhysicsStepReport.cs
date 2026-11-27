using System.Numerics;

namespace XREngine.Components.Movement;

/// <summary>
/// Per-physics-step reconciliation snapshot emitted by <see cref="CharacterMovement3DComponent"/>
/// after the underlying character controller (for example Jolt <c>CharacterVirtual</c>) resolves
/// ground support, slopes, step-up, and wall collisions.
/// </summary>
public readonly record struct CharacterMovementPhysicsStepReport(
    Vector3 FootPosition,
    Vector3 ActualDisplacement,
    Vector3 RequestedDisplacement,
    Vector3 ActualPlanarVelocity,
    Vector3 WallNormal,
    float BlockedRatio,
    bool IsBlocked,
    bool IsGrounded,
    float DeltaTime);
