using XREngine.Rendering.Compute;

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
    GPUPhysicsChainBackendState GpuBackendState,
    bool CpuKernelAvailable,
    PhysicsChainCpuKernelFamily CpuKernelFamily,
    PhysicsChainGpuKernelMask GpuKernelFamilies,
    PhysicsChainQualityTier RequestedQualityTier,
    PhysicsChainQualityTier EffectiveQualityTier,
    PhysicsChainQualityPolicy EffectiveQualityPolicy,
    PhysicsChainCompatibilityFeatures CompatibilityFeatures);
