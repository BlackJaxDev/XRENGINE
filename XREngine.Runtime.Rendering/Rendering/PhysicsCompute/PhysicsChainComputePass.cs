namespace XREngine.Rendering.Compute;

/// <summary>
/// Backend-neutral synchronization contract for a completed physics-chain pass.
/// </summary>
public readonly record struct PhysicsChainComputePass(
    PhysicsChainComputePassKind Kind,
    EMemoryBarrierMask CompletionBarrier);
