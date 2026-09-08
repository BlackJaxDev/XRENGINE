namespace XREngine.Rendering.Vulkan;

/// <summary>Allocation-free stable-bin construction failure evidence.</summary>
internal readonly record struct VulkanPreparedMeshIngressFailure(
    EVulkanPreparedMeshIngressFailureKind Kind,
    int EntryIndex,
    int EntryPassIndex,
    XRFrameBuffer? EntryTarget,
    int PackageExceptionIndex,
    int PackageExceptionCount,
    BackendReadyFramePackageIdentity PackageIdentity,
    long PackageGeneration,
    long PackageSourceRevision,
    BackendReadyOrderedExceptionRecord PackageExceptionRecord,
    XRRenderPipelineInstance? SelectedPipeline);