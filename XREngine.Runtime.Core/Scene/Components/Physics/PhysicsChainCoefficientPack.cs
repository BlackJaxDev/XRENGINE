using System.Numerics;

namespace XREngine.Components;

/// <summary>
/// SIMD/GPU-friendly authored coefficients prepacked once per template.
/// </summary>
public readonly record struct PhysicsChainCoefficientPack(
    Vector4 Dynamics,
    Vector4 CollisionAndLength,
    float BoneLength)
{
    public float Damping => Dynamics.X;
    public float Elasticity => Dynamics.Y;
    public float Stiffness => Dynamics.Z;
    public float Inertia => Dynamics.W;
    public float Friction => CollisionAndLength.X;
    public float Radius => CollisionAndLength.Y;
    public float SegmentLength => CollisionAndLength.Z;
    public float InverseSegmentLength => CollisionAndLength.W;
}
