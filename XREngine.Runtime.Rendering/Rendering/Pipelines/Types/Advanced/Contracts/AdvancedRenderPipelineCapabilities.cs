namespace XREngine.Rendering;

/// <summary>
/// Immutable backend capability snapshot used to select the advanced render pipeline.
/// Required features are explicit; optional acceleration flags never change the logical
/// visibility, scene, or material contracts.
/// </summary>
public readonly record struct AdvancedRenderPipelineCapabilities(
    RuntimeGraphicsApiKind Backend,
    bool RendererAvailable,
    bool SupportsIntegerRenderTargets,
    EAdvancedVisibilityTargetEncoding VisibilityTargetEncoding,
    bool SupportsComputeShaders,
    bool SupportsStorageBuffers,
    EAdvancedIndirectSubmissionMode IndirectSubmission,
    EAdvancedTextureIndirectionMode TextureIndirection,
    EAdvancedSynchronizationMode Synchronization,
    bool SupportsFrameSlotStorage,
    bool SupportsStereoArrayResources,
    EAdvancedShaderFamily ShaderFamily,
    bool SupportsBufferDeviceAddress,
    bool SupportsDescriptorIndexing,
    bool SupportsDescriptorHeap,
    bool SupportsSubgroupOperations,
    bool SupportsMeshShaders,
    bool SupportsAsyncCompute,
    bool SupportsTimelineSemaphores)
{
    /// <summary>
    /// Snapshot used when pipeline selection happens before a renderer is available.
    /// </summary>
    public static AdvancedRenderPipelineCapabilities NoRenderer
        => new(
            Backend: RuntimeGraphicsApiKind.Unknown,
            RendererAvailable: false,
            SupportsIntegerRenderTargets: false,
            VisibilityTargetEncoding: EAdvancedVisibilityTargetEncoding.None,
            SupportsComputeShaders: false,
            SupportsStorageBuffers: false,
            IndirectSubmission: EAdvancedIndirectSubmissionMode.None,
            TextureIndirection: EAdvancedTextureIndirectionMode.None,
            Synchronization: EAdvancedSynchronizationMode.None,
            SupportsFrameSlotStorage: false,
            SupportsStereoArrayResources: false,
            ShaderFamily: EAdvancedShaderFamily.None,
            SupportsBufferDeviceAddress: false,
            SupportsDescriptorIndexing: false,
            SupportsDescriptorHeap: false,
            SupportsSubgroupOperations: false,
            SupportsMeshShaders: false,
            SupportsAsyncCompute: false,
            SupportsTimelineSemaphores: false);

    /// <summary>
    /// Snapshot used by renderer hosts that have not implemented the advanced contract.
    /// </summary>
    public static AdvancedRenderPipelineCapabilities UnsupportedBackend
        => NoRenderer with { RendererAvailable = true };

    /// <summary>
    /// Produces a stable, profiler-friendly description of the selected backend encodings.
    /// </summary>
    public string DescribeSelectedEncodings()
        => $"{Backend}/{VisibilityTargetEncoding}/{IndirectSubmission}/{TextureIndirection}/{Synchronization}/{ShaderFamily}";
}
