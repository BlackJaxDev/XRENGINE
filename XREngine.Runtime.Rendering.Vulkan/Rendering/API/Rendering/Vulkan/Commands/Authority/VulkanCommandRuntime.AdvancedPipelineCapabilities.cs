using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>Command and descriptor capability synthesis for advanced pipeline selection.</summary>
internal sealed partial class VulkanCommandRuntime
{
    internal AdvancedRenderPipelineCapabilities GetAdvancedRenderPipelineCapabilities()
    {
        EAdvancedIndirectSubmissionMode indirectSubmission = DeviceContext.SupportsMeshTaskIndirectCount
            ? EAdvancedIndirectSubmissionMode.MeshTasksIndirectCount
            : DeviceContext.Capabilities.Supports(EVulkanDeviceCapability.DrawIndirectCount)
                ? EAdvancedIndirectSubmissionMode.MultiDrawIndirectCount
                : EAdvancedIndirectSubmissionMode.MultiDrawIndirect;
        EVulkanDescriptorBackend descriptorBackend = ResourceRuntime.Descriptors.ActiveDescriptorBackend;
        EAdvancedTextureIndirectionMode textureIndirection = descriptorBackend switch
        {
            EVulkanDescriptorBackend.DescriptorHeap => EAdvancedTextureIndirectionMode.VulkanDescriptorHeap,
            EVulkanDescriptorBackend.DescriptorIndexing => EAdvancedTextureIndirectionMode.VulkanDescriptorIndexing,
            _ => EAdvancedTextureIndirectionMode.None,
        };
        return new(
            RuntimeGraphicsApiKind.Vulkan, true, true, EAdvancedVisibilityTargetEncoding.R32G32UInt,
            SupportsOrderedComputeWork, true, indirectSubmission, textureIndirection,
            DeviceContext.SupportsSynchronization2 ? EAdvancedSynchronizationMode.VulkanSynchronization2 : EAdvancedSynchronizationMode.VulkanLegacyBarriers,
            true, true, EAdvancedShaderFamily.None, DeviceContext.SupportsBufferDeviceAddress,
            DeviceContext.Capabilities.Supports(EVulkanDeviceCapability.DescriptorIndexing),
            DeviceContext.SupportsDescriptorHeap, false, DeviceContext.SupportsMeshTaskIndirectCount,
            false, DeviceContext.Capabilities.Supports(EVulkanDeviceCapability.TimelineSemaphores));
    }

    internal ERvcDescriptorBackend RvcDescriptorBackend => ResourceRuntime.Descriptors.ActiveDescriptorBackend switch
    {
        EVulkanDescriptorBackend.DescriptorHeap => ERvcDescriptorBackend.DescriptorHeap,
        EVulkanDescriptorBackend.DescriptorIndexing => ERvcDescriptorBackend.DescriptorIndexing,
        _ => ERvcDescriptorBackend.None,
    };

    internal bool SupportsRvcMaterialResourceTable => RvcDescriptorBackend != ERvcDescriptorBackend.None;
    internal bool SupportsRvcVisibilityTargets => DeviceContext.SupportsDynamicRendering && DeviceContext.SupportsSynchronization2 && DeviceContext.SupportsFragmentStoresAndAtomics && SupportsRvcMaterialResourceTable;
    internal bool SupportsRvcOpenXrVisibilityMaskStencil => SupportsRvcVisibilityTargets;
    internal ERvcVulkanProductionFeature ResolveRvcProductionFeatures(bool multiview) => DeviceContext.ResolveRvcProductionFeatures(multiview);
}
