namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Frozen non-device publications produced while the device authority creates
/// and finalizes the logical device. The composition root applies each section
/// once to its owning authority.
/// </summary>
internal sealed record VulkanLogicalDeviceBootstrapResult(
    VulkanLogicalDeviceBootstrapResult.OutputPublication Output,
    VulkanLogicalDeviceBootstrapResult.ResourcePublication Resources,
    VulkanLogicalDeviceBootstrapResult.CommandPublication Commands,
    VulkanLogicalDeviceBootstrapResult.EnginePublication Engine,
    VulkanDiagnosticCapabilitySnapshot Diagnostics,
    VulkanExplicitCapabilityPolicySnapshot ExplicitPolicy,
    VulkanLayeredShadowCapabilityRequest LayeredShadows)
{
    internal readonly record struct OutputPublication(
        bool StreamlineDlssProvisioned,
        bool StreamlineFrameGenerationProvisioned,
        uint StreamlineGraphicsQueueFamily,
        uint StreamlineGraphicsQueueIndex,
        uint StreamlineComputeQueueFamily,
        uint StreamlineComputeQueueIndex,
        uint StreamlineOpticalFlowQueueFamily,
        uint StreamlineOpticalFlowQueueIndex,
        string[] StreamlineRequiredDeviceExtensions,
        string[] StreamlineRequiredFeatures12,
        string[] StreamlineRequiredFeatures13,
        XREngine.Rendering.DLSS.NvidiaDlssManager.Native.StreamlineQueueRequirements StreamlineQueueRequirements,
        bool SwapchainMaintenance1Enabled,
        EVulkanRenderTargetMode RequestedRenderTargetMode,
        bool UseDynamicRenderingRenderTargets,
        bool ObsExternalSharingValidated);

    internal readonly record struct QueryPublication(
        bool OcclusionPreciseAdvertised,
        bool OcclusionPreciseEnabled,
        bool PipelineStatisticsAdvertised,
        bool PipelineStatisticsEnabled,
        bool InheritedQueriesAdvertised,
        bool InheritedQueriesEnabled,
        bool MeshShaderQueriesEnabled,
        bool HostResetAdvertised,
        bool PrimitivesGeneratedAdvertised,
        bool PrimitivesGeneratedEnabled,
        bool PrimitivesGeneratedNonZeroStreamsEnabled);

    internal sealed record DescriptorPublication(
        bool DescriptorIndexingEnabled,
        bool DescriptorHeapExtensionAvailable,
        bool DescriptorHeapDependenciesReady,
        bool DescriptorHeapFeatureSupported,
        bool DescriptorHeapCaptureReplaySupported,
        bool DescriptorHeapShaderUntypedPointersAvailable,
        bool DescriptorHeapNativeApiAvailable,
        string DescriptorHeapNativeApiReason,
        PhysicalDeviceDescriptorHeapPropertiesEXTNative DescriptorHeapProperties,
        VulkanDescriptorHeapNativeFunctions? DescriptorHeapNativeFunctions,
        EVulkanDescriptorBackend RequestedBackend);

    internal readonly record struct ResourcePublication(
        QueryPublication Queries,
        DescriptorPublication Descriptors,
        bool SupportsPipelineCreationCacheControl,
        bool CreatePipelineCache);

    internal readonly record struct CommandPublication(
        bool UseCoreDynamicRenderingCommands,
        bool UseCoreSynchronization2Commands,
        bool DrawIndirectCountEnabled);

    internal readonly record struct EnginePublication(
        bool HasVulkanMultiView,
        bool HasVulkanDepthClipControl,
        bool HasVulkanMemoryDecompression,
        bool HasVulkanCopyMemoryIndirect);
}
