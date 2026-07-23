using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Central descriptor-to-Vulkan mapping and capability validation.
/// </summary>
public static class VulkanQueryDescriptorMapper
{
    private const int QueryTypeResultStatusOnlyKhr = 1_000_023_000;
    private const int QueryTypeTransformFeedbackStreamExt = 1_000_028_004;
    private const int QueryTypePerformanceQueryKhr = 1_000_116_000;
    private const int QueryTypeAccelerationStructureCompactedSizeKhr = 1_000_150_000;
    private const int QueryTypeAccelerationStructureSerializationSizeKhr = 1_000_150_001;
    private const int QueryTypeMeshPrimitivesGeneratedExt = 1_000_328_000;
    private const int QueryTypePrimitivesGeneratedExt = 1_000_382_000;
    private const int QueryTypeAccelerationStructureSerializationBottomLevelPointersKhr = 1_000_386_000;
    private const int QueryTypeAccelerationStructureSizeKhr = 1_000_386_001;
    private const int QueryTypeMicromapSerializationSizeExt = 1_000_396_000;
    private const int QueryTypeMicromapCompactedSizeExt = 1_000_396_001;

    public static VulkanQueryPlan Map(
        in RenderQueryDescriptor descriptor,
        in VulkanQueryCapabilities capabilities,
        uint viewSlotCount = 1u)
    {
        uint occupiedViews = Math.Max(viewSlotCount, 1u);
        return descriptor.Kind switch
        {
            ERenderQueryKind.Occlusion => MapOcclusion(descriptor, capabilities, occupiedViews),
            ERenderQueryKind.Timestamp => MapTimestamp(descriptor, capabilities),
            ERenderQueryKind.ElapsedTime => MapElapsed(descriptor, capabilities),
            ERenderQueryKind.PipelineStatistics => MapPipelineStatistics(descriptor, capabilities),
            ERenderQueryKind.TransformFeedback => MapTransformFeedback(descriptor, capabilities),
            ERenderQueryKind.PrimitivesGenerated => MapPrimitivesGenerated(descriptor, capabilities),
            ERenderQueryKind.MeshPrimitivesGenerated => MapMeshPrimitivesGenerated(descriptor, capabilities),
            ERenderQueryKind.AccelerationStructureProperty => MapAccelerationStructureProperty(descriptor, capabilities),
            ERenderQueryKind.MicromapProperty => MapMicromapProperty(descriptor, capabilities),
            ERenderQueryKind.PerformanceCounter => MapPerformance(descriptor, capabilities),
            ERenderQueryKind.VideoResultStatus => MapVideo(descriptor, capabilities),
            _ => VulkanQueryPlan.Unsupported(descriptor, ERenderQueryReadStatus.Unsupported, "Unknown render-query kind."),
        };
    }

    public static bool IsTimestampStageSupported(
        ulong requestedStageMask,
        in VulkanQueryCapabilities capabilities)
        => capabilities.GraphicsTimestampValidBits != 0u &&
           requestedStageMask != 0ul &&
           (requestedStageMask & (requestedStageMask - 1ul)) == 0ul &&
           (requestedStageMask & ~capabilities.GraphicsSupportedStageMask) == 0ul;

    public static QueryPipelineStatisticFlags MapPipelineStatistics(ERenderPipelineStatistics statistics)
        => (QueryPipelineStatisticFlags)(uint)statistics;

    private static VulkanQueryPlan MapOcclusion(
        in RenderQueryDescriptor descriptor,
        in VulkanQueryCapabilities capabilities,
        uint viewSlotCount)
    {
        bool exact = descriptor.OcclusionMode == EOcclusionResultMode.ExactSamplesPassed;
        if (exact && !capabilities.OcclusionQueryPreciseEnabled)
        {
            string reason = capabilities.OcclusionQueryPreciseAdvertised
                ? "occlusionQueryPrecise is advertised but was not selected for logical-device creation."
                : "occlusionQueryPrecise is not advertised by the physical device.";
            return VulkanQueryPlan.Unsupported(descriptor, ERenderQueryReadStatus.Unsupported, reason);
        }

        QueryControlFlags control = exact ? QueryControlFlags.PreciseBit : QueryControlFlags.None;
        return Supported(
            descriptor,
            QueryType.Occlusion,
            QueryPipelineStatisticFlags.None,
            control,
            1u,
            viewSlotCount,
            viewSlotCount,
            exact ? ERenderQueryAggregation.Sum : ERenderQueryAggregation.AnyNonZero,
            EVulkanQueryRecordingProvider.BeginEnd);
    }

    private static VulkanQueryPlan MapTimestamp(
        in RenderQueryDescriptor descriptor,
        in VulkanQueryCapabilities capabilities)
    {
        if (capabilities.GraphicsTimestampValidBits == 0u)
            return VulkanQueryPlan.Unsupported(descriptor, ERenderQueryReadStatus.Unsupported, "The graphics queue family exposes zero timestamp valid bits.");

        return Supported(
            descriptor,
            QueryType.Timestamp,
            QueryPipelineStatisticFlags.None,
            QueryControlFlags.None,
            1u,
            1u,
            1u,
            ERenderQueryAggregation.Scalar,
            EVulkanQueryRecordingProvider.Timestamp);
    }

    private static VulkanQueryPlan MapElapsed(
        in RenderQueryDescriptor descriptor,
        in VulkanQueryCapabilities capabilities)
    {
        if (capabilities.GraphicsTimestampValidBits == 0u)
            return VulkanQueryPlan.Unsupported(descriptor, ERenderQueryReadStatus.Unsupported, "Portable elapsed time requires timestamp support on the selected queue family.");

        return Supported(
            descriptor,
            QueryType.Timestamp,
            QueryPipelineStatisticFlags.None,
            QueryControlFlags.None,
            1u,
            2u,
            1u,
            ERenderQueryAggregation.TimestampDelta,
            EVulkanQueryRecordingProvider.Timestamp);
    }

    private static VulkanQueryPlan MapPipelineStatistics(
        in RenderQueryDescriptor descriptor,
        in VulkanQueryCapabilities capabilities)
    {
        if (descriptor.Statistics == ERenderPipelineStatistics.None)
            return VulkanQueryPlan.Unsupported(descriptor, ERenderQueryReadStatus.InvalidState, "Pipeline-statistics masks must be nonzero.");
        if ((descriptor.Statistics & ~ERenderPipelineStatistics.All) != 0)
            return VulkanQueryPlan.Unsupported(descriptor, ERenderQueryReadStatus.InvalidState, "Pipeline-statistics masks contain unknown counter bits.");
        if (!capabilities.PipelineStatisticsEnabled)
        {
            string reason = capabilities.PipelineStatisticsAdvertised
                ? "pipelineStatisticsQuery is advertised but was not enabled."
                : "pipelineStatisticsQuery is not advertised.";
            return VulkanQueryPlan.Unsupported(descriptor, ERenderQueryReadStatus.Unsupported, reason);
        }

        ERenderPipelineStatistics meshStatistics = ERenderPipelineStatistics.TaskShaderInvocations |
            ERenderPipelineStatistics.MeshShaderInvocations;
        if ((descriptor.Statistics & meshStatistics) != 0 && !capabilities.MeshShaderQueriesEnabled)
            return VulkanQueryPlan.Unsupported(descriptor, ERenderQueryReadStatus.Unsupported, "Mesh/task pipeline statistics require meshShaderQueries.");

        uint values = RenderQueryResultLayout.CountStatistics(descriptor.Statistics);
        return Supported(
            descriptor,
            QueryType.PipelineStatistics,
            MapPipelineStatistics(descriptor.Statistics),
            QueryControlFlags.None,
            values,
            1u,
            1u,
            ERenderQueryAggregation.ProviderDefined,
            EVulkanQueryRecordingProvider.BeginEnd);
    }

    private static VulkanQueryPlan MapTransformFeedback(
        in RenderQueryDescriptor descriptor,
        in VulkanQueryCapabilities capabilities)
    {
        if (!capabilities.TransformFeedbackExtensionEnabled)
            return VulkanQueryPlan.Unsupported(descriptor, ERenderQueryReadStatus.Unsupported, "VK_EXT_transform_feedback is not enabled.");
        if (!capabilities.TransformFeedbackCommandsLoaded)
            return VulkanQueryPlan.Unsupported(descriptor, ERenderQueryReadStatus.Unsupported, "VK_EXT_transform_feedback commands are not loaded.");
        if (!capabilities.TransformFeedbackQueriesEnabled)
            return VulkanQueryPlan.Unsupported(descriptor, ERenderQueryReadStatus.Unsupported, "transformFeedbackQueries is false.");
        if (descriptor.StreamIndex >= Math.Max(capabilities.MaxTransformFeedbackStreams, 1u))
            return VulkanQueryPlan.Unsupported(descriptor, ERenderQueryReadStatus.InvalidState, "The transform-feedback stream index exceeds maxTransformFeedbackStreams.");

        return Supported(
            descriptor,
            (QueryType)QueryTypeTransformFeedbackStreamExt,
            QueryPipelineStatisticFlags.None,
            QueryControlFlags.None,
            2u,
            1u,
            1u,
            ERenderQueryAggregation.ProviderDefined,
            EVulkanQueryRecordingProvider.TransformFeedbackIndexed);
    }

    private static VulkanQueryPlan MapPrimitivesGenerated(
        in RenderQueryDescriptor descriptor,
        in VulkanQueryCapabilities capabilities)
    {
        if (!capabilities.PrimitivesGeneratedExtensionEnabled)
        {
            string reason = capabilities.PrimitivesGeneratedExtensionAdvertised
                ? "VK_EXT_primitives_generated_query is advertised but not enabled."
                : "VK_EXT_primitives_generated_query is not advertised.";
            return VulkanQueryPlan.Unsupported(descriptor, ERenderQueryReadStatus.Unsupported, reason);
        }
        if (!capabilities.PrimitivesGeneratedQueryEnabled)
            return VulkanQueryPlan.Unsupported(descriptor, ERenderQueryReadStatus.Unsupported, "primitivesGeneratedQuery was not enabled.");
        if (!capabilities.TransformFeedbackCommandsLoaded)
            return VulkanQueryPlan.Unsupported(descriptor, ERenderQueryReadStatus.Unsupported, "Indexed primitives-generated recording commands are not loaded.");
        if (descriptor.StreamIndex != 0u && !capabilities.PrimitivesGeneratedNonZeroStreamsEnabled)
            return VulkanQueryPlan.Unsupported(descriptor, ERenderQueryReadStatus.Unsupported, "Nonzero streams require primitivesGeneratedQueryWithNonZeroStreams.");

        return Supported(
            descriptor,
            (QueryType)QueryTypePrimitivesGeneratedExt,
            QueryPipelineStatisticFlags.None,
            QueryControlFlags.None,
            1u,
            1u,
            1u,
            ERenderQueryAggregation.Scalar,
            EVulkanQueryRecordingProvider.PrimitivesGeneratedIndexed);
    }

    private static VulkanQueryPlan MapMeshPrimitivesGenerated(
        in RenderQueryDescriptor descriptor,
        in VulkanQueryCapabilities capabilities)
    {
        if (!capabilities.MeshShaderExtensionEnabled || !capabilities.MeshShaderCommandsLoaded)
            return VulkanQueryPlan.Unsupported(descriptor, ERenderQueryReadStatus.Unsupported, "VK_EXT_mesh_shader is not enabled with loaded commands.");
        if (!capabilities.MeshShaderQueriesEnabled)
            return VulkanQueryPlan.Unsupported(descriptor, ERenderQueryReadStatus.Unsupported, "meshShaderQueries was not enabled.");

        return Supported(
            descriptor,
            (QueryType)QueryTypeMeshPrimitivesGeneratedExt,
            QueryPipelineStatisticFlags.None,
            QueryControlFlags.None,
            1u,
            1u,
            1u,
            ERenderQueryAggregation.Scalar,
            EVulkanQueryRecordingProvider.BeginEnd);
    }

    private static VulkanQueryPlan MapAccelerationStructureProperty(
        in RenderQueryDescriptor descriptor,
        in VulkanQueryCapabilities capabilities)
    {
        if (!capabilities.AccelerationStructureSubsystemEnabled)
            return VulkanQueryPlan.Unsupported(descriptor, ERenderQueryReadStatus.SubsystemUnavailable, "The acceleration-structure query owner is not enabled in this build.");
        if (!capabilities.AccelerationStructureExtensionEnabled || !capabilities.AccelerationStructureCommandsLoaded)
            return VulkanQueryPlan.Unsupported(descriptor, ERenderQueryReadStatus.Unsupported, "VK_KHR_acceleration_structure is not enabled with loaded property-write commands.");

        QueryType queryType = descriptor.Property switch
        {
            ERenderQueryProperty.CompactedSize => (QueryType)QueryTypeAccelerationStructureCompactedSizeKhr,
            ERenderQueryProperty.SerializationSize => (QueryType)QueryTypeAccelerationStructureSerializationSizeKhr,
            ERenderQueryProperty.CurrentSize => (QueryType)QueryTypeAccelerationStructureSizeKhr,
            ERenderQueryProperty.SerializationBottomLevelPointers => (QueryType)QueryTypeAccelerationStructureSerializationBottomLevelPointersKhr,
            _ => default,
        };
        if ((int)queryType == 0)
            return VulkanQueryPlan.Unsupported(descriptor, ERenderQueryReadStatus.InvalidState, "The acceleration-structure property is not valid for a Vulkan property query.");

        return Supported(descriptor, queryType, QueryPipelineStatisticFlags.None, QueryControlFlags.None, 1u, 1u, 1u, ERenderQueryAggregation.Scalar, EVulkanQueryRecordingProvider.AccelerationStructureProperties);
    }

    private static VulkanQueryPlan MapMicromapProperty(
        in RenderQueryDescriptor descriptor,
        in VulkanQueryCapabilities capabilities)
    {
        if (!capabilities.MicromapSubsystemEnabled)
            return VulkanQueryPlan.Unsupported(descriptor, ERenderQueryReadStatus.SubsystemUnavailable, "The opacity-micromap subsystem is not enabled.");
        if (!capabilities.MicromapExtensionEnabled || !capabilities.MicromapCommandsLoaded)
            return VulkanQueryPlan.Unsupported(descriptor, ERenderQueryReadStatus.Unsupported, "VK_EXT_opacity_micromap is not enabled with loaded property-write commands.");

        QueryType queryType = descriptor.Property switch
        {
            ERenderQueryProperty.CompactedSize => (QueryType)QueryTypeMicromapCompactedSizeExt,
            ERenderQueryProperty.SerializationSize => (QueryType)QueryTypeMicromapSerializationSizeExt,
            _ => default,
        };
        if ((int)queryType == 0)
            return VulkanQueryPlan.Unsupported(descriptor, ERenderQueryReadStatus.InvalidState, "The micromap property is not valid for a Vulkan property query.");

        return Supported(descriptor, queryType, QueryPipelineStatisticFlags.None, QueryControlFlags.None, 1u, 1u, 1u, ERenderQueryAggregation.Scalar, EVulkanQueryRecordingProvider.MicromapProperties);
    }

    private static VulkanQueryPlan MapPerformance(
        in RenderQueryDescriptor descriptor,
        in VulkanQueryCapabilities capabilities)
    {
        if (!capabilities.PerformanceQueryExtensionEnabled || !capabilities.PerformanceQueryFeatureEnabled)
            return VulkanQueryPlan.Unsupported(descriptor, ERenderQueryReadStatus.SubsystemUnavailable, "KHR performance-query enumeration and feature ownership are not enabled.");
        if (!capabilities.PerformanceProfilingLockOwned)
            return VulkanQueryPlan.Unsupported(descriptor, ERenderQueryReadStatus.InvalidState, "The required performance profiling lock is not owned.");
        if (descriptor.ProviderValueCount == 0u)
            return VulkanQueryPlan.Unsupported(descriptor, ERenderQueryReadStatus.InvalidState, "A performance descriptor requires an enumerated nonzero counter count.");

        return Supported(descriptor, (QueryType)QueryTypePerformanceQueryKhr, QueryPipelineStatisticFlags.None, QueryControlFlags.None, descriptor.ProviderValueCount, 1u, 1u, ERenderQueryAggregation.ProviderDefined, EVulkanQueryRecordingProvider.Performance);
    }

    private static VulkanQueryPlan MapVideo(
        in RenderQueryDescriptor descriptor,
        in VulkanQueryCapabilities capabilities)
    {
        if (!capabilities.VideoQueueEnabled)
            return VulkanQueryPlan.Unsupported(descriptor, ERenderQueryReadStatus.SubsystemUnavailable, "No Vulkan video queue/profile owner is active.");
        if (!capabilities.VideoQueryCommandsLoaded)
            return VulkanQueryPlan.Unsupported(descriptor, ERenderQueryReadStatus.Unsupported, "Video result-status commands are not loaded.");

        return Supported(descriptor, (QueryType)QueryTypeResultStatusOnlyKhr, QueryPipelineStatisticFlags.None, QueryControlFlags.None, 1u, 1u, 1u, ERenderQueryAggregation.ProviderDefined, EVulkanQueryRecordingProvider.Video);
    }

    private static VulkanQueryPlan Supported(
        in RenderQueryDescriptor descriptor,
        QueryType queryType,
        QueryPipelineStatisticFlags statistics,
        QueryControlFlags controlFlags,
        uint valuesPerQuery,
        uint queryCount,
        uint viewSlotCount,
        ERenderQueryAggregation aggregation,
        EVulkanQueryRecordingProvider provider)
        => new(
            true,
            queryType,
            statistics,
            controlFlags,
            new(
                descriptor.Kind,
                valuesPerQuery,
                queryCount,
                viewSlotCount,
                checked((int)valuesPerQuery),
                ERenderQueryIntegerWidth.UInt64,
                aggregation,
                descriptor.Statistics,
                descriptor.Property),
            provider,
            null);
}
