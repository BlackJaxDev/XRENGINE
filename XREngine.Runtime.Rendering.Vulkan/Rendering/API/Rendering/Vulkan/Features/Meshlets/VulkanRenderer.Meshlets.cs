using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Gets the mesh shader dialect supported by the Vulkan renderer.
    /// </summary>
    public override EMeshShaderDialect MeshShaderDialect
        => IsDeviceExtensionSupported("VK_EXT_mesh_shader")
            ? EMeshShaderDialect.VulkanEXT
            : EMeshShaderDialect.None;

    /// <summary>
    /// Gets a value indicating whether the Vulkan renderer supports direct mesh task dispatch.
    /// </summary>
    /// <returns>True if direct mesh task dispatch is supported; otherwise, false.</returns>
    public override bool SupportsDirectMeshTaskDispatch()
        => false;

    /// <summary>
    /// Gets a value indicating whether the Vulkan renderer supports indirect-count mesh task dispatch.
    /// </summary>
    /// <returns>True if indirect-count mesh task dispatch is supported; otherwise, false.</returns>
    public override bool SupportsIndirectCountMeshTaskDispatch()
        => SupportsVulkanMeshTaskIndirectCount;

    /// <summary>
    /// Gets a value indicating whether the Vulkan renderer supports production meshlet shaders.
    /// </summary>
    /// <returns>True if production meshlet shaders are supported; otherwise, false.</returns>
    public override bool SupportsProductionMeshletShaders()
        => MeshShaderDialect == EMeshShaderDialect.VulkanEXT;

    /// <summary>
    /// Attempts to draw mesh tasks using indirect-count dispatch.
    /// </summary>
    /// <param name="indirectBuffer">The buffer containing the indirect draw commands.</param>
    /// <param name="countBuffer">The buffer containing the draw count.</param>
    /// <param name="maxDrawCount">The maximum number of draw calls to execute.</param>
    /// <param name="stride">The stride between successive draw commands in the indirect buffer.</param>
    /// <param name="failureReason">Outputs the reason for failure if the draw operation is not supported or fails.</param>
    /// <param name="byteOffset">The byte offset into the indirect buffer where the draw commands start.</param>
    /// <param name="countByteOffset">The byte offset into the count buffer where the draw count is stored.</param>
    /// <returns>True if the draw operation was successfully enqueued; otherwise, false.</returns>
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
            return false;

        if (!SupportsIndirectCountMeshTaskDispatch())
        {
            failureReason = $"classification=explicit-request; {MeshletDispatchUnsupportedReason}";
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

    /// <summary>
    /// Gets a value indicating the reason why meshlet dispatch is unsupported.
    /// </summary>
    public override string MeshletDispatchUnsupportedReason
        => MeshShaderDialect == EMeshShaderDialect.VulkanEXT
            ? SupportsIndirectCountMeshTaskDispatch()
                ? "VK_EXT_mesh_shader indirect-count dispatch is available."
                : "VK_EXT_mesh_shader is visible, but task/mesh shader features or vkCmdDrawMeshTasksIndirectCountEXT dispatch are unavailable."
            : "VK_EXT_mesh_shader is not available on the active Vulkan device.";

    /// <summary>
    /// Gets the descriptor backend used by the RVC (Retinal Visibility Cache) system.
    /// </summary>
    public override ERvcDescriptorBackend RvcDescriptorBackend
        => ActiveDescriptorBackend switch
        {
            EVulkanDescriptorBackend.DescriptorHeap => ERvcDescriptorBackend.DescriptorHeap,
            EVulkanDescriptorBackend.DescriptorIndexing => ERvcDescriptorBackend.DescriptorIndexing,
            _ => ERvcDescriptorBackend.None,
        };

    /// <summary>
    /// Gets a value indicating whether the RVC system supports the material resource table.
    /// </summary>
    public override bool SupportsRvcMaterialResourceTable
        => RvcDescriptorBackend != ERvcDescriptorBackend.None;

    /// <summary>
    /// Gets a value indicating whether the RVC system supports visibility targets.
    /// </summary>
    public override bool SupportsRvcVisibilityTargets
        => SupportsDynamicRendering &&
           SupportsSynchronization2 &&
           SupportsFragmentStoresAndAtomics &&
           SupportsRvcMaterialResourceTable;

    /// <summary>
    /// Gets a value indicating whether the RVC system supports the OpenXR visibility mask stencil feature.
    /// </summary>
    public override bool SupportsRvcOpenXrVisibilityMaskStencil
        => SupportsRvcVisibilityTargets;

    /// <summary>
    /// Gets the Vulkan production features supported by the RVC (Retinal Visibility Cache) system.
    /// </summary>
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
