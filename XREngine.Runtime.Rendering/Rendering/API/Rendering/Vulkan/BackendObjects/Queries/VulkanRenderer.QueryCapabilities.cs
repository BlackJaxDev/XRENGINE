using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private bool _queryOcclusionPreciseAdvertised;
    private bool _queryOcclusionPreciseEnabled;
    private bool _queryPipelineStatisticsAdvertised;
    private bool _queryPipelineStatisticsEnabled;
    private bool _queryInheritedQueriesAdvertised;
    private bool _queryInheritedQueriesEnabled;
    private bool _queryHostResetAdvertised;
    private bool _queryMeshShaderQueriesEnabled;
    private bool _queryPrimitivesGeneratedAdvertised;
    private bool _queryPrimitivesGeneratedEnabled;
    private bool _queryPrimitivesGeneratedNonZeroStreamsEnabled;

    public VulkanQueryCapabilities QueryCapabilities { get; private set; }

    private void RefreshVulkanQueryCapabilities()
    {
        if (_physicalDevice.Handle == 0)
        {
            QueryCapabilities = VulkanQueryCapabilities.Unsupported;
            return;
        }

        Api!.GetPhysicalDeviceProperties(_physicalDevice, out PhysicalDeviceProperties properties);
        uint graphicsFamily = FamilyQueueIndices.GraphicsFamilyIndex ?? 0u;
        uint familyCount = 0u;
        Api.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, ref familyCount, null);
        uint timestampValidBits = 0u;
        QueueFlags graphicsQueueFlags = 0;
        if (graphicsFamily < familyCount)
        {
            QueueFamilyProperties* families = stackalloc QueueFamilyProperties[checked((int)familyCount)];
            Api.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, ref familyCount, families);
            timestampValidBits = families[graphicsFamily].TimestampValidBits;
            graphicsQueueFlags = families[graphicsFamily].QueueFlags;
        }

        bool transformFeedbackExtensionEnabled = _enabledDeviceExtensions.Contains("VK_EXT_transform_feedback", StringComparer.Ordinal);
        bool primitivesGeneratedExtensionAdvertised = _availableDeviceExtensions.Contains("VK_EXT_primitives_generated_query", StringComparer.Ordinal);
        bool primitivesGeneratedExtensionEnabled = _enabledDeviceExtensions.Contains("VK_EXT_primitives_generated_query", StringComparer.Ordinal);
        bool meshShaderExtensionEnabled = _enabledDeviceExtensions.Contains("VK_EXT_mesh_shader", StringComparer.Ordinal);
        bool accelerationStructureExtensionEnabled = _enabledDeviceExtensions.Contains("VK_KHR_acceleration_structure", StringComparer.Ordinal);

        QueryCapabilities = new(
            _queryOcclusionPreciseAdvertised,
            _queryOcclusionPreciseEnabled,
            _queryPipelineStatisticsAdvertised,
            _queryPipelineStatisticsEnabled,
            _queryInheritedQueriesAdvertised,
            _queryInheritedQueriesEnabled,
            _queryHostResetAdvertised,
            SupportsHostQueryReset,
            SupportsSynchronization2,
            graphicsFamily,
            timestampValidBits,
            ResolveTimestampStageMask(
                graphicsQueueFlags,
                transformFeedbackExtensionEnabled,
                meshShaderExtensionEnabled,
                accelerationStructureExtensionEnabled,
                SupportsSynchronization2),
            Math.Max(properties.Limits.TimestampPeriod, 0.0001f),
            transformFeedbackExtensionEnabled,
            _extTransformFeedback is not null,
            SupportsTransformFeedbackQueries,
            Math.Max(_transformFeedbackProperties.MaxTransformFeedbackStreams, 1u),
            primitivesGeneratedExtensionAdvertised,
            primitivesGeneratedExtensionEnabled,
            _queryPrimitivesGeneratedEnabled,
            _queryPrimitivesGeneratedNonZeroStreamsEnabled,
            meshShaderExtensionEnabled,
            _extMeshShader is not null,
            _queryMeshShaderQueriesEnabled,
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
            QueryCapabilities.OcclusionQueryPreciseAdvertised,
            QueryCapabilities.OcclusionQueryPreciseEnabled,
            QueryCapabilities.PipelineStatisticsAdvertised,
            QueryCapabilities.PipelineStatisticsEnabled,
            QueryCapabilities.InheritedQueriesAdvertised,
            QueryCapabilities.InheritedQueriesEnabled,
            QueryCapabilities.HostQueryResetAdvertised,
            QueryCapabilities.HostQueryResetEnabled,
            QueryCapabilities.GraphicsTimestampValidBits,
            QueryCapabilities.TimestampPeriodNanoseconds,
            QueryCapabilities.Synchronization2Enabled,
            QueryCapabilities.TransformFeedbackExtensionEnabled,
            QueryCapabilities.TransformFeedbackCommandsLoaded,
            QueryCapabilities.TransformFeedbackQueriesEnabled,
            QueryCapabilities.PrimitivesGeneratedExtensionAdvertised,
            QueryCapabilities.PrimitivesGeneratedExtensionEnabled,
            QueryCapabilities.PrimitivesGeneratedQueryEnabled,
            QueryCapabilities.MeshShaderQueriesEnabled);
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
