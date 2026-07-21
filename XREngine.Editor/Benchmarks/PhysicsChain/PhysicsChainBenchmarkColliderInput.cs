using System.Numerics;

namespace XREngine.Editor.Benchmarks.PhysicsChain;

/// <summary>Stable authored collider input generated for one benchmark case.</summary>
public readonly record struct PhysicsChainBenchmarkColliderInput(
    PhysicsChainBenchmarkColliderKind Kind,
    Vector3 Position,
    Vector3 Dimensions,
    Quaternion Rotation);
