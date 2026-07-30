namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <inheritdoc />
    public override AdvancedRenderPipelineCapabilities GetAdvancedRenderPipelineCapabilities()
    {
        EAdvancedIndirectSubmissionMode indirectSubmission = SupportsMeshletDispatch()
            ? EAdvancedIndirectSubmissionMode.MeshTasksIndirectCount
            : SupportsIndirectCountDraw()
                ? EAdvancedIndirectSubmissionMode.MultiDrawIndirectCount
                : EAdvancedIndirectSubmissionMode.MultiDrawIndirect;

        EAdvancedTextureIndirectionMode textureIndirection = ActiveDescriptorBackend switch
        {
            EVulkanDescriptorBackend.DescriptorHeap =>
                EAdvancedTextureIndirectionMode.VulkanDescriptorHeap,
            EVulkanDescriptorBackend.DescriptorIndexing =>
                EAdvancedTextureIndirectionMode.VulkanDescriptorIndexing,
            _ => EAdvancedTextureIndirectionMode.None,
        };

        EAdvancedSynchronizationMode synchronization = SupportsSynchronization2
            ? EAdvancedSynchronizationMode.VulkanSynchronization2
            : EAdvancedSynchronizationMode.VulkanLegacyBarriers;

        return new(
            Backend: RuntimeGraphicsApiKind.Vulkan,
            RendererAvailable: true,
            SupportsIntegerRenderTargets: true,
            VisibilityTargetEncoding: EAdvancedVisibilityTargetEncoding.R32G32UInt,
            SupportsComputeShaders: SupportsOrderedComputeWork,
            SupportsStorageBuffers: true,
            IndirectSubmission: indirectSubmission,
            TextureIndirection: textureIndirection,
            Synchronization: synchronization,
            SupportsFrameSlotStorage: true,
            SupportsStereoArrayResources: true,
            // The frame contract exists, but production visibility shaders do not.
            // Keep selection unavailable until the backend implements the full family.
            ShaderFamily: EAdvancedShaderFamily.None,
            SupportsBufferDeviceAddress: SupportsBufferDeviceAddress,
            SupportsDescriptorIndexing: SupportsDescriptorIndexing,
            SupportsDescriptorHeap: SupportsDescriptorHeap,
            SupportsSubgroupOperations: false,
            SupportsMeshShaders: SupportsMeshletDispatch(),
            SupportsAsyncCompute: false,
            SupportsTimelineSemaphores: DeviceCapabilities.Supports(EVulkanDeviceCapability.TimelineSemaphores));
    }
}
