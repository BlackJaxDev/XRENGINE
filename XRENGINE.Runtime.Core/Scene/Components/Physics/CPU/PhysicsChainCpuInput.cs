using System.Numerics;

namespace XREngine.Components;

/// <summary>
/// Blittable per-instance inputs consumed by the scalar CPU reference step.
/// </summary>
public readonly record struct PhysicsChainCpuInput(
    float DeltaTime,
    float Speed,
    float ObjectScale,
    float Weight,
    Vector3 Gravity,
    Vector3 ExternalForce,
    Vector3 ObjectMove,
    uint ResetState,
    uint CollisionEnabled = 1u);
