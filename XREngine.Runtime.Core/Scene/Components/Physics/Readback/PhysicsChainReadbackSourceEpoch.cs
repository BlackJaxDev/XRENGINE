namespace XREngine.Components;

/// <summary>
/// Captures every backend-owned generation that can invalidate a gather after
/// it was scheduled but before its staging fence signals.
/// </summary>
public readonly record struct PhysicsChainReadbackSourceEpoch(
    uint BackendGeneration,
    uint ArenaGeneration,
    uint LayoutGeneration)
{
    public bool IsValid
        => BackendGeneration != 0u && ArenaGeneration != 0u && LayoutGeneration != 0u;
}
