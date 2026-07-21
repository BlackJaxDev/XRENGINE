namespace XREngine.Components;

/// <summary>
/// Non-blocking completion primitive supplied by the active backend. The
/// readback layer intentionally exposes no wait operation.
/// </summary>
public interface IPhysicsChainReadbackFence
{
    PhysicsChainReadbackFenceStatus Poll();
}
