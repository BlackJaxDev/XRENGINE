using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Device-only post-create publication and normalization.  The composition root
/// supplies integration requirements as immutable values; this authority never
/// re-queries OpenXR, Streamline, output, or renderer state.
/// </summary>
internal sealed partial class VulkanDeviceContext
{
    /// <summary>
    /// Checks the selected device's published extension snapshot, querying the
    /// native device only during pre-selection bootstrap when no snapshot exists.
    /// </summary>
    internal bool IsDeviceExtensionSupported(Vk api, string extensionName)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionName);

        if (AvailableDeviceExtensions.Count != 0)
            return AvailableDeviceExtensions.Contains(extensionName);
        if (PhysicalDevice.Handle == 0)
            return false;

        VulkanDeviceExtensionSet extensions =
            VulkanDeviceCapabilityQuery.EnumerateExtensions(api, PhysicalDevice);
        return extensions.Contains(extensionName);
    }

    /// <summary>
    /// Produces the unique extension list passed to <c>vkCreateDevice</c> while
    /// resolving the mutually exclusive EXT and KHR/core buffer-device-address
    /// paths.
    /// </summary>
    internal static string[] NormalizeDeviceExtensionSelection(
        IReadOnlyList<string> requestedExtensions,
        bool vulkan12PromotedToCore)
    {
        ArgumentNullException.ThrowIfNull(requestedExtensions);

        bool khrBufferDeviceAddressRequested = requestedExtensions.Any(
            static extension => string.Equals(
                extension,
                "VK_KHR_buffer_device_address",
                StringComparison.Ordinal));

        List<string> normalized = new(requestedExtensions.Count);
        for (int i = 0; i < requestedExtensions.Count; ++i)
        {
            string extension = requestedExtensions[i];
            if (string.IsNullOrWhiteSpace(extension))
                continue;

            if (string.Equals(
                    extension,
                    "VK_EXT_buffer_device_address",
                    StringComparison.Ordinal) &&
                (vulkan12PromotedToCore || khrBufferDeviceAddressRequested))
            {
                continue;
            }

            if (!normalized.Contains(extension, StringComparer.Ordinal))
                normalized.Add(extension);
        }

        return [.. normalized];
    }

    /// <summary>
    /// Loads device command tables and reconciles command availability with the
    /// negotiated feature state. No output, frame, resource, or renderer state
    /// participates in this native-device transition.
    /// </summary>
    internal void LoadAndFinalizeExtensionFunctions(
        Vk api,
        in VulkanDeviceExtensionLoadRequest request)
    {
        LoadExtensionFunctions(api);
        bool dynamicRenderingEnabled = EnabledDeviceExtensions.Contains("VK_KHR_dynamic_rendering");
        if (dynamicRenderingEnabled && request.RequireKhrDynamicRenderingCommands &&
            ExtensionFunctions.KhrDynamicRendering is null)
        {
            Debug.VulkanWarning("[Vulkan] Failed to load VK_KHR_dynamic_rendering extension command table.");
            MutableCapabilities._supportsDynamicRendering = false;
        }

        bool synchronization2Enabled = EnabledDeviceExtensions.Contains("VK_KHR_synchronization2");
        if (synchronization2Enabled && request.RequireKhrSynchronization2Commands &&
            ExtensionFunctions.KhrSynchronization2 is null)
        {
            Debug.VulkanWarning("[Vulkan] Failed to load VK_KHR_synchronization2 extension command table.");
            MutableCapabilities._supportsSynchronization2 = false;
        }

        bool indirectCountCoreFeaturesReady =
            MutableCapabilities._supportsMultiDrawIndirect &&
            MutableCapabilities._supportsDrawIndirectFirstInstance;
        if (request.EnableCoreDrawIndirectCount && indirectCountCoreFeaturesReady)
        {
            MutableCapabilities._usesCoreDrawIndirectCountCommands = true;
            MutableCapabilities._supportsDrawIndirectCount = true;
        }
        else if (EnabledDeviceExtensions.Contains("VK_KHR_draw_indirect_count"))
        {
            MutableCapabilities._supportsDrawIndirectCount =
                indirectCountCoreFeaturesReady && ExtensionFunctions.KhrDrawIndirectCount is not null;
            if (!MutableCapabilities._supportsDrawIndirectCount)
            {
                Debug.VulkanWarning(
                    "[Vulkan] VK_KHR_draw_indirect_count is unavailable for engine submission because its command table or required core features are missing.");
            }
        }

        if (EnabledDeviceExtensions.Contains(ExtMeshShader.ExtensionName))
        {
            MutableCapabilities._supportsVulkanMeshTaskIndirectCount =
                MutableCapabilities._supportsVulkanTaskShaderFeature &&
                MutableCapabilities._supportsVulkanMeshShaderFeature &&
                ExtensionFunctions.ExtMeshShader is not null;
            if (!MutableCapabilities._supportsVulkanMeshTaskIndirectCount)
                Debug.VulkanWarning("[Vulkan] VK_EXT_mesh_shader command table or negotiated features are unavailable for indirect mesh task dispatch.");
        }

        if (EnabledDeviceExtensions.Contains(ExtTransformFeedback.ExtensionName) &&
            ExtensionFunctions.ExtTransformFeedback is null)
        {
            MutableCapabilities._supportsTransformFeedback = false;
            MutableCapabilities._supportsTransformFeedbackGeometryStreams = false;
            MutableCapabilities._supportsTransformFeedbackQueries = false;
            MutableCapabilities._supportsTransformFeedbackDraw = false;
            MutableCapabilities._transformFeedbackProperties = default;
        }

        MutableCapabilities._supportsExternalMemoryWin32 =
            EnabledDeviceExtensions.Contains("VK_KHR_external_memory_win32") &&
            ExtensionFunctions.KhrExternalMemoryWin32 is not null;
        MutableCapabilities._supportsExternalSemaphoreWin32 =
            EnabledDeviceExtensions.Contains("VK_KHR_external_semaphore_win32") &&
            ExtensionFunctions.KhrExternalSemaphoreWin32 is not null;

        if (MutableCapabilities._supportsNvMemoryDecompression &&
            ExtensionFunctions.NvMemoryDecompression is null)
        {
            MutableCapabilities._supportsNvMemoryDecompression = false;
            MutableCapabilities._nvMemoryDecompressionMethods = 0;
            MutableCapabilities._nvMaxMemoryDecompressionIndirectCount = 0;
        }

        if (MutableCapabilities._supportsNvCopyMemoryIndirect &&
            ExtensionFunctions.NvCopyMemoryIndirect is null)
        {
            MutableCapabilities._supportsNvCopyMemoryIndirect = false;
            MutableCapabilities._nvCopyMemoryIndirectSupportedQueues = 0;
        }

        if (EnabledDeviceExtensions.Contains("VK_EXT_device_fault") &&
            DeviceFaultFacility.SupportsExtDeviceFault &&
            ExtensionFunctions.ExtDeviceFault is null)
        {
            DeviceFaultFacility.PublishExtSupport(
                supportsDeviceFault: false,
                supportsVendorBinary: false);
        }

        if (EnabledDeviceExtensions.Contains("VK_KHR_device_fault") &&
            DeviceFaultFacility.SupportsKhrDeviceFault &&
            !TryLoadKhrDeviceFaultCommandTable(
                api,
                out _,
                out _))
        {
            DeviceFaultFacility.PublishKhrSupport(
                supportsDeviceFault: false,
                supportsVendorBinary: false,
                supportsReportMasked: false,
                supportsDeviceLostOnMasked: false,
                DeviceFaultFacility.KhrDeviceFaultMaxReportCount);
        }

        if (MutableCapabilities._supportsNvDiagnosticCheckpoints &&
            ExtensionFunctions.NvDeviceDiagnosticCheckpoints is null)
        {
            MutableCapabilities._supportsNvDiagnosticCheckpoints = false;
        }

        if (MutableCapabilities._supportsNvCopyMemoryIndirect &&
            !MutableCapabilities._supportsBufferDeviceAddress)
        {
            MutableCapabilities._supportsNvCopyMemoryIndirect = false;
            MutableCapabilities._nvCopyMemoryIndirectSupportedQueues = 0;
        }
    }

    /// <summary>
    /// Freezes the effective post-loader capability state using only the
    /// immutable extension requirements supplied by the composition root.
    /// </summary>
    internal void PublishLogicalDeviceCapabilities(
        VulkanDeviceExtensionRequirements extensionRequirements)
    {
        ArgumentNullException.ThrowIfNull(extensionRequirements);

        EVulkanDeviceCapability capabilities = EVulkanDeviceCapability.None;
        Include(ref capabilities, EVulkanDeviceCapability.Anisotropy, MutableCapabilities._supportsAnisotropy);
        Include(ref capabilities, EVulkanDeviceCapability.MultipleGraphicsQueues, SupportsMultipleGraphicsQueues);
        Include(ref capabilities, EVulkanDeviceCapability.TimelineSemaphores, MutableCapabilities._supportsTimelineSemaphores);
        Include(ref capabilities, EVulkanDeviceCapability.Synchronization2, MutableCapabilities._supportsSynchronization2);
        Include(ref capabilities, EVulkanDeviceCapability.DescriptorIndexing, MutableCapabilities._supportsDescriptorIndexing);
        Include(ref capabilities, EVulkanDeviceCapability.DescriptorHeap, MutableCapabilities._supportsDescriptorHeap);
        Include(ref capabilities, EVulkanDeviceCapability.BufferDeviceAddress, MutableCapabilities._supportsBufferDeviceAddress);
        Include(ref capabilities, EVulkanDeviceCapability.DynamicRendering, MutableCapabilities._supportsDynamicRendering);
        Include(ref capabilities, EVulkanDeviceCapability.DynamicRenderingLocalRead, MutableCapabilities._supportsDynamicRenderingLocalRead);
        Include(ref capabilities, EVulkanDeviceCapability.Maintenance4, MutableCapabilities._supportsMaintenance4);
        Include(ref capabilities, EVulkanDeviceCapability.Maintenance5, MutableCapabilities._supportsMaintenance5);
        Include(ref capabilities, EVulkanDeviceCapability.MemoryBudget, MutableCapabilities._supportsMemoryBudget);
        Include(ref capabilities, EVulkanDeviceCapability.MemoryPriority, MutableCapabilities._supportsMemoryPriority);
        Include(ref capabilities, EVulkanDeviceCapability.ShaderObject, MutableCapabilities._supportsShaderObject);
        Include(ref capabilities, EVulkanDeviceCapability.AccelerationStructure, MutableCapabilities._supportsAccelerationStructure);
        Include(ref capabilities, EVulkanDeviceCapability.RayTracingPipeline, MutableCapabilities._supportsRayTracingPipeline);
        Include(ref capabilities, EVulkanDeviceCapability.RayQuery, MutableCapabilities._supportsRayQuery);
        Include(ref capabilities, EVulkanDeviceCapability.DeviceGeneratedCommands, MutableCapabilities._supportsDeviceGeneratedCommands);
        Include(ref capabilities, EVulkanDeviceCapability.DeviceFault, DeviceFaultFacility.SupportsKhrDeviceFault || DeviceFaultFacility.SupportsExtDeviceFault);
        Include(ref capabilities, EVulkanDeviceCapability.DeviceFaultVendorBinary, DeviceFaultFacility.SupportsKhrDeviceFaultVendorBinary || DeviceFaultFacility.SupportsExtDeviceFaultVendorBinary);
        Include(ref capabilities, EVulkanDeviceCapability.DeviceAddressBindingReport, MutableCapabilities._supportsDeviceAddressBindingReport);
        Include(ref capabilities, EVulkanDeviceCapability.NvDiagnosticCheckpoints, MutableCapabilities._supportsNvDiagnosticCheckpoints);
        Include(ref capabilities, EVulkanDeviceCapability.NvDiagnosticsConfig, MutableCapabilities._supportsNvDiagnosticsConfig);
        Include(ref capabilities, EVulkanDeviceCapability.NvMemoryDecompression, MutableCapabilities._supportsNvMemoryDecompression);
        Include(ref capabilities, EVulkanDeviceCapability.NvCopyMemoryIndirect, MutableCapabilities._supportsNvCopyMemoryIndirect);
        Include(ref capabilities, EVulkanDeviceCapability.DepthClipControl, MutableCapabilities._supportsDepthClipControl);
        Include(ref capabilities, EVulkanDeviceCapability.MeshShader, MutableCapabilities._supportsVulkanMeshShaderFeature);
        Include(ref capabilities, EVulkanDeviceCapability.GraphicsPipelineLibrary, MutableCapabilities._supportsGraphicsPipelineLibrary);
        Include(ref capabilities, EVulkanDeviceCapability.TransformFeedback, MutableCapabilities._supportsTransformFeedback);
        Include(ref capabilities, EVulkanDeviceCapability.HostQueryReset, MutableCapabilities._supportsHostQueryReset);
        Include(ref capabilities, EVulkanDeviceCapability.FragmentShadingRate, MutableCapabilities._supportsVulkanFragmentShadingRate);
        Include(ref capabilities, EVulkanDeviceCapability.FragmentDensityMap, MutableCapabilities._supportsVulkanFragmentDensityMap);
        Include(ref capabilities, EVulkanDeviceCapability.IndexTypeUint8, MutableCapabilities._supportsIndexTypeUint8);
        Include(ref capabilities, EVulkanDeviceCapability.DrawIndirectCount, MutableCapabilities._supportsDrawIndirectCount);
        Include(ref capabilities, EVulkanDeviceCapability.MultiDrawIndirect, MutableCapabilities._supportsMultiDrawIndirect);
        Include(ref capabilities, EVulkanDeviceCapability.DrawIndirectFirstInstance, MutableCapabilities._supportsDrawIndirectFirstInstance);
        Include(ref capabilities, EVulkanDeviceCapability.GeometryShader, MutableCapabilities._supportsGeometryShader);
        Include(ref capabilities, EVulkanDeviceCapability.FragmentStoresAndAtomics, MutableCapabilities._supportsFragmentStoresAndAtomics);
        Include(ref capabilities, EVulkanDeviceCapability.VertexPipelineStoresAndAtomics, MutableCapabilities._supportsVertexPipelineStoresAndAtomics);
        Include(ref capabilities, EVulkanDeviceCapability.Vulkan14, MutableCapabilities._supportsVulkan14);

        EVulkanDeviceFallback fallbacks = EVulkanDeviceFallback.None;
        if (!SupportsMultipleGraphicsQueues)
            fallbacks |= EVulkanDeviceFallback.SingleGraphicsQueue;
        if (!MutableCapabilities._supportsSynchronization2)
            fallbacks |= EVulkanDeviceFallback.LegacySynchronization;
        if (!MutableCapabilities._supportsDynamicRendering)
            fallbacks |= EVulkanDeviceFallback.LegacyRenderPass;
        if (!MutableCapabilities._supportsDescriptorIndexing && !MutableCapabilities._supportsDescriptorHeap)
            fallbacks |= EVulkanDeviceFallback.ClassicDescriptors;
        if (!DeviceFaultFacility.SupportsKhrDeviceFault && !DeviceFaultFacility.SupportsExtDeviceFault)
            fallbacks |= EVulkanDeviceFallback.DeviceFaultDiagnosticsUnavailable;

        VulkanDeviceCapabilities publishedCapabilities = new(
            AvailableDeviceExtensions,
            new VulkanDeviceExtensionSet(extensionRequirements.RequiredExtensions),
            EnabledDeviceExtensions,
            capabilities,
            fallbacks);
        PublishCapabilities(publishedCapabilities);
    }

    private static void Include(
        ref EVulkanDeviceCapability destination,
        EVulkanDeviceCapability capability,
        bool enabled)
    {
        if (enabled)
            destination |= capability;
    }
}
