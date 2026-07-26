namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable snapshot of query-related physical-device, logical-device,
/// extension-command, and queue-family capabilities.
/// </summary>
public readonly record struct VulkanQueryCapabilities(
    bool OcclusionQueryPreciseAdvertised,
    bool OcclusionQueryPreciseEnabled,
    bool PipelineStatisticsAdvertised,
    bool PipelineStatisticsEnabled,
    bool InheritedQueriesAdvertised,
    bool InheritedQueriesEnabled,
    bool HostQueryResetAdvertised,
    bool HostQueryResetEnabled,
    bool Synchronization2Enabled,
    uint GraphicsQueueFamily,
    uint GraphicsTimestampValidBits,
    ulong GraphicsSupportedStageMask,
    double TimestampPeriodNanoseconds,
    bool TransformFeedbackExtensionEnabled,
    bool TransformFeedbackCommandsLoaded,
    bool TransformFeedbackQueriesEnabled,
    uint MaxTransformFeedbackStreams,
    bool PrimitivesGeneratedExtensionAdvertised,
    bool PrimitivesGeneratedExtensionEnabled,
    bool PrimitivesGeneratedQueryEnabled,
    bool PrimitivesGeneratedNonZeroStreamsEnabled,
    bool MeshShaderExtensionEnabled,
    bool MeshShaderCommandsLoaded,
    bool MeshShaderQueriesEnabled,
    bool AccelerationStructureExtensionEnabled,
    bool AccelerationStructureCommandsLoaded,
    bool AccelerationStructureSubsystemEnabled,
    bool MicromapExtensionEnabled,
    bool MicromapCommandsLoaded,
    bool MicromapSubsystemEnabled,
    bool PerformanceQueryExtensionEnabled,
    bool PerformanceQueryFeatureEnabled,
    bool PerformanceProfilingLockOwned,
    bool VideoQueueEnabled,
    bool VideoQueryCommandsLoaded)
{
    public static VulkanQueryCapabilities Unsupported { get; } = new();
}
