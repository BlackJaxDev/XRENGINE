using System.Numerics;

namespace XREngine.Components;

/// <summary>
/// Blittable backend-neutral particle output from one reference step.
/// </summary>
public struct PhysicsChainCpuOutput
{
    public Vector3 CurrentPosition;
    public Vector3 PreviousPosition;
    public uint IsColliding;
}
