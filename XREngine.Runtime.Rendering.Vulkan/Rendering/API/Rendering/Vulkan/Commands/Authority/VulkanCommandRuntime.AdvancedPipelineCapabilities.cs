using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>Command and descriptor capability synthesis for advanced pipeline selection.</summary>
internal sealed partial class VulkanCommandRuntime
{
    internal bool IsAdvancedVisibilityProductionPromoted => false;

    internal AdvancedRenderPipelineCapabilities GetAdvancedRenderPipelineCapabilities()
    {
        EAdvancedIndirectSubmissionMode indirectSubmission = DeviceContext.SupportsMeshTaskIndirectCount
            ? EAdvancedIndirectSubmissionMode.MeshTasksIndirectCount
            : DeviceContext.Capabilities.Supports(EVulkanDeviceCapability.DrawIndirectCount)
                ? EAdvancedIndirectSubmissionMode.MultiDrawIndirectCount
                : EAdvancedIndirectSubmissionMode.MultiDrawIndirect;
        VulkanAdvancedSceneResourceRuntime advancedResources =
            ResourceRuntime.AdvancedSceneResources;
        bool supportsAdvancedFrameStorage = advancedResources.IsReady &&
            ResourceRuntime.FrameDataArena is { IsActive: true };
        EAdvancedTextureIndirectionMode textureIndirection =
            advancedResources.TextureIndirectionMode;
        return new(
            RuntimeGraphicsApiKind.Vulkan, true, true, EAdvancedVisibilityTargetEncoding.R32G32UInt,
            SupportsOrderedComputeWork, true, indirectSubmission, textureIndirection,
            DeviceContext.SupportsSynchronization2 ? EAdvancedSynchronizationMode.VulkanSynchronization2 : EAdvancedSynchronizationMode.VulkanLegacyBarriers,
            supportsAdvancedFrameStorage, false,
            // The realized visibility ABI is currently one mono family per
            // primary frame plan. Global pipeline selection has no output-
            // family reservation identity, so advertising the family here
            // could select it independently for multiple mono outputs and
            // reject the combined stream only at preflight. Keep production
            // promotion fail-closed until that cardinality is represented.
            EAdvancedShaderFamily.None,
            DeviceContext.SupportsBufferDeviceAddress,
            advancedResources.IsReady,
            false, false, DeviceContext.SupportsMeshTaskIndirectCount,
            false, DeviceContext.Capabilities.Supports(EVulkanDeviceCapability.TimelineSemaphores));
    }

    internal bool CanAdmitAdvancedVisibilityFamily()
    {
        if (!DeviceContext.IsOperational ||
            !DeviceContext.Capabilities.Supports(EVulkanDeviceCapability.DrawIndirectCount) ||
            !ResourceRuntime.AdvancedSceneResources.IsReady ||
            !ResourceRuntime.AdvancedVisibilityResources.IsReady)
        {
            return false;
        }

        // Target-specific image/view closure is sealed against the accepted
        // frame plan. Capability synthesis covers only device/runtime support;
        // it must not allocate or intern per-frame image views.
        VulkanAdvancedVisibilityPipelineRuntime pipelines =
            ResourceRuntime.AdvancedVisibilityPipelines;
        if (!pipelines.TryGetComputePipelines(out _, out _, out _) ||
            !pipelines.TryGetLateVisibilityComputePipelines(out _, out _, out _) ||
            !pipelines.TryGetRasterProgram(
                EAdvancedMaterialCoverageMode.Opaque,
                meshlet: false,
                out _,
                out _) ||
            !pipelines.TryGetRasterProgram(
                EAdvancedMaterialCoverageMode.Masked,
                meshlet: false,
                out _,
                out _))
        {
            return false;
        }

        return !DeviceContext.SupportsMeshTaskIndirectCount ||
            pipelines.TryGetRasterProgram(
                EAdvancedMaterialCoverageMode.Opaque,
                meshlet: true,
                out _,
                out _) &&
            pipelines.TryGetRasterProgram(
                EAdvancedMaterialCoverageMode.Masked,
                meshlet: true,
                out _,
                out _);
    }

    internal ERvcDescriptorBackend RvcDescriptorBackend => ResourceRuntime.Descriptors.ActiveDescriptorBackend switch
    {
        EVulkanDescriptorBackend.DescriptorHeap => ERvcDescriptorBackend.DescriptorHeap,
        EVulkanDescriptorBackend.DescriptorIndexing => ERvcDescriptorBackend.DescriptorIndexing,
        _ => ERvcDescriptorBackend.None,
    };

    internal bool SupportsRvcMaterialResourceTable => RvcDescriptorBackend != ERvcDescriptorBackend.None;
    internal bool SupportsRvcVisibilityTargets =>
        DeviceContext.SupportsDynamicRendering &&
        DeviceContext.SupportsSynchronization2 &&
        DeviceContext.SupportsFragmentStoresAndAtomics &&
        DeviceContext.SupportsVertexPipelineStoresAndAtomics &&
        SupportsRvcMaterialResourceTable;
    internal bool SupportsRvcOpenXrVisibilityMaskStencil => SupportsRvcVisibilityTargets;
    internal ERvcVulkanProductionFeature ResolveRvcProductionFeatures(bool multiview) => DeviceContext.ResolveRvcProductionFeatures(multiview);
}
