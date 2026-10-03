namespace XREngine.Components;

/// <summary>Authority that produces collider poses for broadphase generation.</summary>
public enum PhysicsChainColliderPoseOwner : byte
{
    Cpu,
    Gpu,
}
