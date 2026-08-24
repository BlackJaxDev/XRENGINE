using XREngine.Rendering.DLSS;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Immutable cross-authority facts used to negotiate and create one Vulkan
/// logical device. The device authority never retains this request.
/// </summary>
internal sealed record VulkanLogicalDeviceBootstrapRequest(
    VulkanDeviceExtensionRequirements Extensions,
    VulkanLogicalDeviceBootstrapRequest.OutputRequirements Output,
    VulkanLogicalDeviceBootstrapRequest.StreamlineRequirements Streamline,
    VulkanLogicalDeviceBootstrapRequest.FeaturePolicyFacts FeaturePolicy,
    VulkanDiagnosticOptions Diagnostics,
    VulkanLogicalDeviceBootstrapRequest.LayeredShadowPolicy LayeredShadows)
{
    internal readonly record struct OutputRequirements(
        bool RequiresPresentQueue,
        bool RequiresSwapchainOutput,
        EVulkanRenderTargetMode RequestedRenderTargetMode,
        bool SwapchainMaintenance1Requested,
        bool ValidateObsExternalSharing,
        bool RequireObsExternalSharing);

    internal sealed record StreamlineRequirements(
        StreamlineRequirementSet Active,
        StreamlineRequirementSet WithoutFrameGeneration,
        StreamlineRequirementSet Disabled,
        bool DlssExplicitlyRequested,
        bool FrameGenerationExplicitlyRequested);

    internal sealed record StreamlineRequirementSet(
        bool DlssProvisioned,
        bool FrameGenerationProvisioned,
        string[] RequiredDeviceExtensions,
        string[] RequiredFeatures12,
        string[] RequiredFeatures13,
        NvidiaDlssManager.Native.StreamlineQueueRequirements QueueRequirements)
    {
        internal static StreamlineRequirementSet Empty { get; } = new(
            false,
            false,
            [],
            [],
            [],
            default);
    }

    internal readonly record struct FeaturePolicyFacts(
        EVulkanCapabilityTier RequestedCapabilityTier,
        EVulkanDescriptorBackend RequestedDescriptorBackend,
        EVulkanProgramBindingBackend RequestedProgramBindingBackend,
        EVulkanFoveationBackend RequestedFoveationBackend,
        EVulkanRayTracingBackend RequestedRayTracingBackend,
        EVulkanGeometryFetchMode ActiveGeometryFetchMode,
        bool EnableDescriptorIndexing,
        bool EnableBindlessMaterialTable,
        bool RequireBindlessMaterialTable,
        bool EnableRtxIoVulkanDecompression,
        bool EnableRtxIoVulkanCopyMemoryIndirect,
        EVulkanCapabilityTier? ExplicitCapabilityTier,
        EVulkanDescriptorBackend? ExplicitDescriptorBackend,
        EVulkanProgramBindingBackend? ExplicitProgramBindingBackend,
        EVulkanFoveationBackend? ExplicitFoveationBackend,
        EVulkanRayTracingBackend? ExplicitRayTracingBackend);

    internal readonly record struct LayeredShadowPolicy(bool PublishRuntimeState);
}
