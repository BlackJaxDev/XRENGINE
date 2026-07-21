using XREngine.Rendering.Compute;

namespace XREngine.Components;

public partial class PhysicsChainComponent
{
    /// <summary>
    /// Returns the selected backend, retained kernel families, quality policy, and
    /// all opt-in compatibility work without allocating or reading GPU state back.
    /// </summary>
    public PhysicsChainRuntimeDiagnostics GetRuntimeDiagnostics()
    {
        PhysicsChainBackendStatus runtimeStatus = PhysicsChainBackendStatus.Uninitialized;
        if (World is not null
            && PhysicsChainWorld.TryGet(World, out PhysicsChainWorld? scheduler)
            && scheduler is not null
            && scheduler.TryGetRegistration(_runtimeHandle, out _, out PhysicsChainOutput output))
            runtimeStatus = output.BackendStatus;

        PhysicsChainCpuInstance cpuInstance = default;
        bool cpuKernelAvailable = _cpuBackend is not null
            && _cpuBackend.TryGetInstance(_cpuBackendHandle, out cpuInstance);
        PhysicsChainCpuKernelFamily cpuKernel = cpuKernelAvailable
            ? cpuInstance.KernelFamily
            : PhysicsChainCpuKernelFamily.ScalarLinear;
        PhysicsChainRuntimeBackend backend = UseGPU
            ? UseBatchedDispatcher
                ? PhysicsChainRuntimeBackend.GpuBatched
                : PhysicsChainRuntimeBackend.GpuStandalone
            : PhysicsChainRuntimeBackend.CpuDataOriented;
        GPUPhysicsChainBackendState gpuBackendState = UseGPU
            ? GPUPhysicsChainDispatcher.Instance.BackendStatus.State
            : GPUPhysicsChainBackendState.NotEvaluated;

        PhysicsChainCompatibilityFeatures compatibility = PhysicsChainCompatibilityFeatures.None;
        if (CpuTransformMirrorEnabled)
            compatibility |= PhysicsChainCompatibilityFeatures.CpuTransformMirror;
        if (GpuSyncToBones)
            compatibility |= PhysicsChainCompatibilityFeatures.GpuBoneReadback;
        if (DebugDrawChains)
            compatibility |= PhysicsChainCompatibilityFeatures.PerChainDebugRendering;

        return new PhysicsChainRuntimeDiagnostics(
            _runtimeHandle,
            backend,
            runtimeStatus,
            gpuBackendState,
            cpuKernelAvailable,
            cpuKernel,
            ResolveGpuKernelFamilies(),
            _qualityTier,
            _effectiveQualityTier,
            EffectiveQualityPolicy,
            compatibility);
    }

    private PhysicsChainGpuKernelMask ResolveGpuKernelFamilies()
    {
        PhysicsChainGpuKernelMask mask = PhysicsChainGpuKernelMask.None;
        for (int treeIndex = 0; treeIndex < _particleTrees.Count; ++treeIndex)
        {
            List<Particle> particles = _particleTrees[treeIndex].Particles;
            bool shortLinear = particles.Count <= 32;
            for (int particleIndex = 1; shortLinear && particleIndex < particles.Count; ++particleIndex)
                shortLinear = particles[particleIndex].ParentIndex == particleIndex - 1;
            mask |= shortLinear
                ? PhysicsChainGpuKernelMask.ShortLinear
                : PhysicsChainGpuKernelMask.BranchedOrLong;
        }
        return mask;
    }
}
