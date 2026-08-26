using System.Numerics;

namespace XREngine.Components;

/// <summary>
/// Immutable solver data for one particle in a physics-chain template.
/// Dynamic positions and velocities live in instance state, not here.
/// </summary>
public readonly record struct PhysicsChainTemplateParticle(
    int ParentIndex,
    int Depth,
    int BoneIndex,
    int ChildCount,
    float SegmentLength,
    float InverseSegmentLength,
    float BoneLength,
    float Damping,
    float Elasticity,
    float Stiffness,
    float Inert,
    float Friction,
    float Radius,
    Vector3 RestOffset,
    Quaternion RestRotation);
