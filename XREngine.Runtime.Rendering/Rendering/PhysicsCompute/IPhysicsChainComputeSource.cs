using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Renderer-facing view of a physics-chain simulation owner.
/// CPU simulation remains outside the rendering assembly; the dispatcher only
/// consumes immutable submission data and publishes GPU results through this contract.
/// </summary>
public interface IPhysicsChainComputeSource
{
    Guid ID { get; }
    IRuntimeWorldContext? World { get; }
    PhysicsChainRuntimeHandle RuntimeHandle { get; }
    IPhysicsChainReadbackCoordinator? ReadbackCoordinator { get; }
    int UpdateMode { get; }
    bool UseBatchedDispatcher { get; }
    bool HasGpuDrivenRenderers { get; }
    bool DebugDrawChains { get; }
    int GpuDebugInterpolationMode { get; }

    float GetGpuDebugInterpolationAlpha();
    bool RequiresGpuReadback();
    void NotifyGpuReadbackUnavailable(string reason);
    void ApplyReadbackData(
        ReadOnlySpan<GPUPhysicsChainDispatcher.GPUParticleData> readbackData,
        int generation,
        long submissionId);
    void AppendBatchedGpuDrivenBonePaletteBindings(
        int particleBaseOffset,
        List<GPUPhysicsChainDispatcher.GpuDrivenRendererPaletteBinding> bindings);
    void ClearBatchedGpuDrivenBonePaletteSources();
    bool PublishGpuDrivenBoneMatrices(
        XRDataBuffer? particlesBuffer,
        XRDataBuffer? transformMatricesBuffer,
        int particleBaseOffset,
        bool includeCompletePalettes = true,
        IPhysicsChainComputeBackend? backend = null);
}
