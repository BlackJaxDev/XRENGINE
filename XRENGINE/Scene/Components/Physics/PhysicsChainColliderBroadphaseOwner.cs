namespace XREngine.Components;

/// <summary>Execution domain selected for collider candidate generation.</summary>
public enum PhysicsChainColliderBroadphaseOwner : byte
{
    None,
    Cpu,
    Gpu,
}
