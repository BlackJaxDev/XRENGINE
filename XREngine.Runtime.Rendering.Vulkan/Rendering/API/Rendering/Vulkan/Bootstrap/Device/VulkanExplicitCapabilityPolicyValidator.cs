namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Validates explicitly requested Vulkan capability and backend policies
/// against an immutable post-device-creation snapshot.
/// </summary>
internal static class VulkanExplicitCapabilityPolicyValidator
{
    internal static void Validate(in VulkanExplicitCapabilityPolicySnapshot snapshot)
    {
        bool productionTierReady =
            snapshot.Vulkan13PromotedToCore &&
            snapshot.DynamicRenderingEnabled &&
            snapshot.Synchronization2Enabled &&
            snapshot.TimelineSemaphoreEnabled &&
            snapshot.DescriptorIndexingEnabled &&
            snapshot.BufferDeviceAddressEnabled &&
            snapshot.DrawIndirectCountEnabled &&
            snapshot.Maintenance4Enabled;
        bool vulkan14OptInTierReady =
            snapshot.Vulkan14PromotedToCore &&
            snapshot.DynamicRenderingLocalReadEnabled &&
            snapshot.Maintenance5Enabled;
        bool vulkan14ExperimentalTierReady =
            vulkan14OptInTierReady &&
            snapshot.DescriptorHeapExtensionAvailable &&
            snapshot.DescriptorHeapDependenciesReady &&
            snapshot.DescriptorHeapNativeApiAvailable &&
            snapshot.ShaderObjectFeatureSupported;

        if (snapshot.ExplicitCapabilityTier is { } tier)
        {
            if (tier == EVulkanCapabilityTier.Vulkan13Production && !productionTierReady)
            {
                ThrowExplicitCapabilityMissing(
                    VulkanFeatureProfile.CapabilityTierEnvVar,
                    tier.ToString(),
                    "Vulkan 1.3 production tier requires dynamic rendering, Sync2, timeline semaphores, descriptor indexing, buffer device address, draw indirect count, and maintenance4.");
            }

            if (tier == EVulkanCapabilityTier.Vulkan14OptInBaseline && !vulkan14OptInTierReady)
            {
                ThrowExplicitCapabilityMissing(
                    VulkanFeatureProfile.CapabilityTierEnvVar,
                    tier.ToString(),
                    "Vulkan 1.4 opt-in tier requires Vulkan 1.4, dynamic rendering local read, and maintenance5.");
            }

            if (tier == EVulkanCapabilityTier.Vulkan14Experimental && !vulkan14ExperimentalTierReady)
            {
                ThrowExplicitCapabilityMissing(
                    VulkanFeatureProfile.CapabilityTierEnvVar,
                    tier.ToString(),
                    "Vulkan 1.4 experimental tier requires the opt-in tier plus descriptor heap binding support and shader-object capability.");
            }
        }

        if (snapshot.ExplicitDescriptorBackend is { } descriptorBackend)
        {
            if (descriptorBackend == EVulkanDescriptorBackend.DescriptorIndexing && !snapshot.DescriptorIndexingEnabled)
            {
                ThrowExplicitCapabilityMissing(
                    VulkanFeatureProfile.DescriptorBackendEnvVar,
                    descriptorBackend.ToString(),
                    "Descriptor indexing was explicitly requested, but the descriptor indexing feature set is unavailable.");
            }

            if (descriptorBackend == EVulkanDescriptorBackend.DescriptorHeap)
            {
                string reason = !snapshot.DescriptorHeapExtensionAvailable
                    ? "VK_EXT_descriptor_heap is not exposed by the selected physical device."
                    : !snapshot.DescriptorHeapDependenciesReady
                        ? "VK_EXT_descriptor_heap dependencies are incomplete; the path needs Vulkan 1.4 or maintenance5/extended flags plus buffer device address/Vulkan 1.2 and shader untyped pointers support."
                        : !snapshot.SupportsDescriptorHeap
                            ? "VK_EXT_descriptor_heap is exposed, but native entry points, feature enablement, or heap storage initialization failed."
                            : snapshot.ActiveDescriptorBackend != EVulkanDescriptorBackend.DescriptorHeap
                                ? snapshot.DescriptorBackendFallbackReason
                                : string.Empty;

                if (string.IsNullOrWhiteSpace(reason))
                    return;

                ThrowExplicitCapabilityMissing(
                    VulkanFeatureProfile.DescriptorBackendEnvVar,
                    descriptorBackend.ToString(),
                    reason);
            }
        }

        if (snapshot.ExplicitProgramBindingBackend == EVulkanProgramBindingBackend.ShaderObjects)
        {
            string reason = snapshot.ShaderObjectFeatureSupported
                ? "VK_EXT_shader_object is available, but the renderer shader-object program-binding backend is not implemented yet."
                : "VK_EXT_shader_object is unavailable or its shaderObject feature bit is false.";
            ThrowExplicitCapabilityMissing(
                VulkanFeatureProfile.ProgramBindingBackendEnvVar,
                EVulkanProgramBindingBackend.ShaderObjects.ToString(),
                reason);
        }

        if (snapshot.ExplicitFoveationBackend is { } foveationBackend)
        {
            if (foveationBackend == EVulkanFoveationBackend.FragmentShadingRate)
            {
                string reason = snapshot.FragmentShadingRateSupported
                    ? "Fragment shading rate is available, but the Vulkan VRS/foveation backend is not implemented yet."
                    : "VK_KHR_fragment_shading_rate is unavailable or no fragment shading-rate feature bit is supported.";
                ThrowExplicitCapabilityMissing(
                    VulkanFeatureProfile.FoveationBackendEnvVar,
                    foveationBackend.ToString(),
                    reason);
            }

            if (foveationBackend == EVulkanFoveationBackend.FragmentDensityMap)
            {
                string reason = snapshot.FragmentDensityMapSupported
                    ? "Fragment density map is available, but the Vulkan density-map foveation backend is not implemented yet."
                    : "VK_EXT_fragment_density_map is unavailable or its feature bit is false.";
                ThrowExplicitCapabilityMissing(
                    VulkanFeatureProfile.FoveationBackendEnvVar,
                    foveationBackend.ToString(),
                    reason);
            }
        }

        if (snapshot.ExplicitRayTracingBackend is { } rayTracingBackend)
        {
            if (rayTracingBackend == EVulkanRayTracingBackend.RayTracingPipeline)
            {
                string reason = snapshot.AccelerationStructureSupported && snapshot.RayTracingPipelineSupported
                    ? "KHR ray tracing pipeline support is available, but the Vulkan ray-tracing backend is not implemented yet."
                    : "KHR ray tracing pipeline support requires acceleration structures, ray tracing pipeline, and deferred host operations.";
                ThrowExplicitCapabilityMissing(
                    VulkanFeatureProfile.RayTracingBackendEnvVar,
                    rayTracingBackend.ToString(),
                    reason);
            }

            if (rayTracingBackend == EVulkanRayTracingBackend.RayQuery)
            {
                string reason = snapshot.AccelerationStructureSupported && snapshot.RayQuerySupported
                    ? "Ray query support is available, but the Vulkan ray-query backend is not implemented yet."
                    : "KHR ray query support requires acceleration structures and the rayQuery feature bit.";
                ThrowExplicitCapabilityMissing(
                    VulkanFeatureProfile.RayTracingBackendEnvVar,
                    rayTracingBackend.ToString(),
                    reason);
            }
        }
    }

    private static void ThrowExplicitCapabilityMissing(string environmentVariable, string value, string reason)
    {
        Debug.VulkanWarning(
            "[Vulkan] Capability.ExplicitRequest state=explicitly-required-missing env={0} value={1} reason='{2}'",
            environmentVariable,
            value,
            reason);
        throw new InvalidOperationException(
            $"Vulkan capability request {environmentVariable}={value} cannot be satisfied: {reason}");
    }
}
