namespace XREngine.Components;

/// <summary>
/// Allocation-free per-chain snapshot for editor inspection and profiler annotations.
/// It reports requested execution independently from backend readiness, so failures
/// cannot be mistaken for an authorized CPU fallback.
/// </summary>
public readonly record struct PhysicsChainRuntimeDiagnostics(
    PhysicsChainRuntimeHandle Handle,
    PhysicsChainRuntimeBackend Backend,
    PhysicsChainBackendStatus RuntimeStatus,
    PhysicsChainGpuBackendState GpuBackendState,
    bool CpuKernelAvailable,
    PhysicsChainCpuKernelFamily CpuKernelFamily,
    PhysicsChainGpuKernelMask GpuKernelFamilies,
    PhysicsChainQualityTier RequestedQualityTier,
    PhysicsChainQualityTier EffectiveQualityTier,
    PhysicsChainQualityPolicy EffectiveQualityPolicy,
    PhysicsChainCompatibilityFeatures CompatibilityFeatures)
{
    public long TemplateId { get; init; }
    public PhysicsChainArenaSlice StateSlice { get; init; }
    public PhysicsChainArenaSlice PaletteSlice { get; init; }
    public PhysicsChainArenaSlice PreviousPaletteSlice { get; init; }
    public PhysicsChainArenaHandle BoundsSlot { get; init; }
    public uint OutputGeneration { get; init; }
    public long OutputSimulationFrame { get; init; }
    public bool IsSleeping { get; init; }
    public PhysicsChainRenderingDiagnostics Rendering { get; init; }
}
