namespace XREngine.Components;

/// <summary>
/// Stable public metadata for one world-owned CPU backend instance.
/// </summary>
public readonly record struct PhysicsChainCpuInstance(
    PhysicsChainArenaHandle Handle,
    ulong TemplateContentHash,
    int TreeCount,
    int ParticleCount,
    int ColliderCount,
    long SimulationFrame,
    uint OutputGeneration,
    PhysicsChainCpuKernelFamily KernelFamily)
{
    public bool IsValid => Handle.IsValid && TreeCount > 0 && ParticleCount > 0 && ColliderCount >= 0 && OutputGeneration != 0u;
}
