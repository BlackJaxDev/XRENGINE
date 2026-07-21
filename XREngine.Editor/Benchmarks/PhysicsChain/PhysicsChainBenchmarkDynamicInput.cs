using System.Numerics;

namespace XREngine.Editor.Benchmarks.PhysicsChain;

/// <summary>Deterministic per-chain input sampled at a fixed simulation frame.</summary>
public readonly record struct PhysicsChainBenchmarkDynamicInput(
    Vector3 RootPosition,
    Quaternion RootRotation,
    Vector3 ExternalForce,
    int ColliderSetIndex,
    bool IsActive,
    bool IsVisible);
