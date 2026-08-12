using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal unsafe sealed partial class VulkanQueryAuthority
{
    internal void RefreshCapabilities()
    {
        VulkanBackendObjectContext context = RequireBackendContext();
        VulkanDeviceContext device = context.DeviceContext;
        if (device.PhysicalDevice.Handle == 0)
        {
            Capabilities = VulkanQueryCapabilities.Unsupported;
            return;
        }

        context.Api.GetPhysicalDeviceProperties(device.PhysicalDevice, out PhysicalDeviceProperties properties);
        uint graphicsFamily = device.QueueFamilies.GraphicsFamilyIndex ?? 0u;
        uint familyCount = 0u;
        context.Api.GetPhysicalDeviceQueueFamilyProperties(device.PhysicalDevice, ref familyCount, null);
        uint timestampValidBits = 0u;
        QueueFlags graphicsQueueFlags = 0;
        if (graphicsFamily < familyCount)
        {
            QueueFamilyProperties[] families = new QueueFamilyProperties[checked((int)familyCount)];
            fixed (QueueFamilyProperties* familiesPtr = families)
            {
                context.Api.GetPhysicalDeviceQueueFamilyProperties(device.PhysicalDevice, ref familyCount, familiesPtr);
                timestampValidBits = families[graphicsFamily].TimestampValidBits;
                graphicsQueueFlags = families[graphicsFamily].QueueFlags;
            }
        }

        bool transformFeedbackExtensionEnabled = device.EnabledDeviceExtensions.Contains("VK_EXT_transform_feedback");
        bool primitivesGeneratedExtensionAdvertised = device.AvailableDeviceExtensions.Contains("VK_EXT_primitives_generated_query");
        bool primitivesGeneratedExtensionEnabled = device.EnabledDeviceExtensions.Contains("VK_EXT_primitives_generated_query");
        bool meshShaderExtensionEnabled = device.EnabledDeviceExtensions.Contains("VK_EXT_mesh_shader");
        bool accelerationStructureExtensionEnabled = device.EnabledDeviceExtensions.Contains("VK_KHR_acceleration_structure");
        bool supportsSynchronization2 = device.MutableCapabilities._supportsSynchronization2;

        Capabilities = new(
            OcclusionPreciseAdvertised,
            OcclusionPreciseEnabled,
            PipelineStatisticsAdvertised,
            PipelineStatisticsEnabled,
            InheritedQueriesAdvertised,
            InheritedQueriesEnabled,
            HostResetAdvertised,
            device.MutableCapabilities._supportsHostQueryReset,
            supportsSynchronization2,
            graphicsFamily,
            timestampValidBits,
            ResolveTimestampStageMask(
                graphicsQueueFlags,
                transformFeedbackExtensionEnabled,
                meshShaderExtensionEnabled,
                accelerationStructureExtensionEnabled,
                supportsSynchronization2),
            Math.Max(properties.Limits.TimestampPeriod, 0.0001f),
            transformFeedbackExtensionEnabled,
            device.ExtensionFunctions.ExtTransformFeedback is not null,
            device.MutableCapabilities._supportsTransformFeedbackQueries,
            Math.Max(device.MutableCapabilities._transformFeedbackProperties.MaxTransformFeedbackStreams, 1u),
            primitivesGeneratedExtensionAdvertised,
            primitivesGeneratedExtensionEnabled,
            PrimitivesGeneratedEnabled,
            PrimitivesGeneratedNonZeroStreamsEnabled,
            meshShaderExtensionEnabled,
            device.ExtensionFunctions.ExtMeshShader is not null,
            MeshShaderQueriesEnabled,
            accelerationStructureExtensionEnabled,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false);

        Debug.Vulkan(
            "[Vulkan.QueryCapabilities] precise={0}/{1} pipelineStats={2}/{3} inherited={4}/{5} hostReset={6}/{7} timestamps={8}bits@{9:F4}ns sync2={10} transformFeedback={11}/{12}/{13} primitivesGenerated={14}/{15}/{16} meshQueries={17} specialized=owner-required.",
            Capabilities.OcclusionQueryPreciseAdvertised,
            Capabilities.OcclusionQueryPreciseEnabled,
            Capabilities.PipelineStatisticsAdvertised,
            Capabilities.PipelineStatisticsEnabled,
            Capabilities.InheritedQueriesAdvertised,
            Capabilities.InheritedQueriesEnabled,
            Capabilities.HostQueryResetAdvertised,
            Capabilities.HostQueryResetEnabled,
            Capabilities.GraphicsTimestampValidBits,
            Capabilities.TimestampPeriodNanoseconds,
            Capabilities.Synchronization2Enabled,
            Capabilities.TransformFeedbackExtensionEnabled,
            Capabilities.TransformFeedbackCommandsLoaded,
            Capabilities.TransformFeedbackQueriesEnabled,
            Capabilities.PrimitivesGeneratedExtensionAdvertised,
            Capabilities.PrimitivesGeneratedExtensionEnabled,
            Capabilities.PrimitivesGeneratedQueryEnabled,
            Capabilities.MeshShaderQueriesEnabled);
    }

    private static ulong ResolveTimestampStageMask(
        QueueFlags queueFlags,
        bool transformFeedbackEnabled,
        bool meshShaderEnabled,
        bool accelerationStructureEnabled,
        bool synchronization2Enabled)
    {
        // VkPipelineStageFlagBits2 values are used directly so the capability
        // snapshot stays valid across Silk.NET aliases for promoted stage names.
        const ulong topOfPipe = 0x00000001ul;
        const ulong bottomOfPipe = 0x00002000ul;
        const ulong allCommands = 0x00010000ul;
        const ulong coreGraphics = 0x000087FEul;
        const ulong allTransfer = 0x00001000ul;
        const ulong transferOperations = 0x0000000F00000000ul;
        const ulong computeShader = 0x00000800ul;
        const ulong taskAndMeshShader = 0x00180000ul;
        const ulong transformFeedback = 0x01000000ul;
        const ulong accelerationStructureBuild = 0x02000000ul;

        ulong mask = topOfPipe | bottomOfPipe | allCommands;
        if ((queueFlags & QueueFlags.GraphicsBit) != 0)
            mask |= coreGraphics;
        if ((queueFlags & QueueFlags.ComputeBit) != 0)
            mask |= computeShader;
        if ((queueFlags & (QueueFlags.GraphicsBit | QueueFlags.ComputeBit | QueueFlags.TransferBit)) != 0)
            mask |= allTransfer | (synchronization2Enabled ? transferOperations : 0ul);
        if (meshShaderEnabled && (queueFlags & QueueFlags.GraphicsBit) != 0)
            mask |= taskAndMeshShader;
        if (transformFeedbackEnabled && (queueFlags & QueueFlags.GraphicsBit) != 0)
            mask |= transformFeedback;
        if (accelerationStructureEnabled && (queueFlags & (QueueFlags.GraphicsBit | QueueFlags.ComputeBit)) != 0)
            mask |= accelerationStructureBuild;
        return mask;
    }
}
