using System.Numerics;

namespace XREngine.Components;

/// <summary>
/// Blittable backend-owned Verlet state for one particle.
/// </summary>
public struct PhysicsChainCpuState
{
    public Vector3 Position;
    public Vector3 PreviousPosition;
    public uint IsColliding;
}
