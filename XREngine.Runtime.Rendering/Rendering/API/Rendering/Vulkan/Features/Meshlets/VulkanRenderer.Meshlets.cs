using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    public override EMeshShaderDialect MeshShaderDialect
        => IsDeviceExtensionSupported("VK_EXT_mesh_shader")
            ? EMeshShaderDialect.VulkanEXT
            : EMeshShaderDialect.None;

    public override bool SupportsDirectMeshTaskDispatch()
        => false;

    public override bool SupportsIndirectCountMeshTaskDispatch()
        => SupportsVulkanMeshTaskIndirectCount;

    public override bool SupportsProductionMeshletShaders()
        => MeshShaderDialect == EMeshShaderDialect.VulkanEXT;

    public override bool TryDrawMeshTasksIndirectCount(
        XRDataBuffer indirectBuffer,
        XRDataBuffer countBuffer,
        uint maxDrawCount,
        uint stride,
        out string failureReason,
        nuint byteOffset = 0,
        nuint countByteOffset = 0)
    {
        if (!ValidateMeshTasksIndirectCountArgs(
            indirectBuffer,
            countBuffer,
            maxDrawCount,
            stride,
            byteOffset,
            countByteOffset,
            out failureReason))
        {
            return false;
        }

        if (!SupportsIndirectCountMeshTaskDispatch())
        {
            failureReason = MeshletDispatchUnsupportedReason;
            Debug.VulkanWarning(failureReason);
            return false;
        }

        var vkIndirectBuffer = GenericToAPI<VkDataBuffer>(indirectBuffer);
        var vkCountBuffer = GenericToAPI<VkDataBuffer>(countBuffer);
        if (vkIndirectBuffer is null || vkCountBuffer is null)
        {
            failureReason = "Vulkan mesh-task indirect-count dispatch requires Vulkan-backed buffers.";
            Debug.VulkanWarning(failureReason);
            return false;
        }

        vkIndirectBuffer.Generate();
        vkCountBuffer.Generate();

        FrameOpContext context = CaptureFrameOpContext();
        int passIndex = RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex;
        EnqueueFrameOp(new MeshTaskDispatchIndirectCountOp(
            EnsureValidPassIndex(passIndex, "MeshTaskDispatchIndirectCount", context.PassMetadata),
            vkIndirectBuffer,
            vkCountBuffer,
            maxDrawCount,
            stride,
            byteOffset,
            countByteOffset,
            CaptureGlobalMaterialTextureDescriptorBindingForNextFrameOp(),
            context));

        failureReason = string.Empty;
        return true;
    }

    public override string MeshletDispatchUnsupportedReason
        => MeshShaderDialect == EMeshShaderDialect.VulkanEXT
            ? SupportsIndirectCountMeshTaskDispatch()
                ? "VK_EXT_mesh_shader indirect-count dispatch is available."
                : "VK_EXT_mesh_shader is visible, but task/mesh shader features or vkCmdDrawMeshTasksIndirectCountEXT dispatch are unavailable."
            : "VK_EXT_mesh_shader is not available on the active Vulkan device.";

    public override ERvcDescriptorBackend RvcDescriptorBackend
        => ActiveDescriptorBackend switch
        {
            EVulkanDescriptorBackend.DescriptorHeap => ERvcDescriptorBackend.DescriptorHeap,
            EVulkanDescriptorBackend.DescriptorIndexing => ERvcDescriptorBackend.DescriptorIndexing,
            _ => ERvcDescriptorBackend.None,
        };

    public override bool SupportsRvcMaterialResourceTable
        => RvcDescriptorBackend != ERvcDescriptorBackend.None;

    public override bool SupportsRvcVisibilityTargets
        => SupportsDynamicRendering &&
           SupportsSynchronization2 &&
           SupportsFragmentStoresAndAtomics &&
           SupportsRvcMaterialResourceTable;

    public override bool SupportsRvcOpenXrVisibilityMaskStencil
        => SupportsRvcVisibilityTargets;

    public override ERvcVulkanProductionFeature RvcVulkanProductionFeatures
    {
        get
        {
            ERvcVulkanProductionFeature features = ERvcVulkanProductionFeature.None;
            if (RuntimeEngine.Rendering.State.HasVulkanMultiView)
                features |= ERvcVulkanProductionFeature.Multiview;
            if (SupportsDynamicRendering)
                features |= ERvcVulkanProductionFeature.DynamicRendering;
            if (SupportsSynchronization2)
                features |= ERvcVulkanProductionFeature.Synchronization2;
            if (SupportsDescriptorIndexing)
                features |= ERvcVulkanProductionFeature.DescriptorIndexing;
            if (SupportsVulkanFragmentShadingRate)
                features |= ERvcVulkanProductionFeature.FragmentShadingRate;
            if (SupportsVulkanFragmentDensityMap)
                features |= ERvcVulkanProductionFeature.FragmentDensityMap;
            if (SupportsProductionMeshletShaders())
                features |= ERvcVulkanProductionFeature.MeshShader;
            if (_supportsTimelineSemaphores)
                features |= ERvcVulkanProductionFeature.TimelineSemaphore;

            return features;
        }
    }
}
