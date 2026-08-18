using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.NV;
using System.Text;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Reports the final immutable device snapshot after creation policy and command
/// loading have both completed.
/// </summary>
internal static class VulkanDeviceCapabilityReporter
{
    private static readonly string[] ReportedModernCapabilityExtensionNames =
    [
        "VK_KHR_multiview", "VK_KHR_external_memory", "VK_KHR_external_semaphore",
        "VK_KHR_external_memory_win32", "VK_KHR_external_semaphore_win32",
        "VK_KHR_draw_indirect_count", "VK_KHR_synchronization2",
        "VK_KHR_shader_draw_parameters", "VK_EXT_shader_viewport_index_layer",
        "VK_EXT_index_type_uint8", "VK_KHR_index_type_uint8", "VK_EXT_descriptor_indexing",
        "VK_EXT_descriptor_heap", "VK_KHR_shader_untyped_pointers", "VK_EXT_descriptor_buffer",
        "VK_EXT_shader_object", "VK_KHR_buffer_device_address", "VK_KHR_dynamic_rendering",
        "VK_KHR_dynamic_rendering_local_read", "VK_KHR_maintenance4", "VK_KHR_maintenance5",
        "VK_KHR_extended_flags", "VK_EXT_depth_clip_control", "VK_KHR_pipeline_library",
        "VK_EXT_graphics_pipeline_library", "VK_EXT_pipeline_creation_cache_control",
        "VK_EXT_transform_feedback", "VK_EXT_primitives_generated_query",
        "VK_KHR_fragment_shading_rate", "VK_EXT_fragment_density_map", "VK_EXT_mesh_shader",
        "VK_EXT_memory_budget", "VK_EXT_memory_priority", "VK_KHR_acceleration_structure",
        "VK_KHR_ray_tracing_pipeline", "VK_KHR_ray_query", "VK_KHR_deferred_host_operations",
        "VK_EXT_device_generated_commands", "VK_NV_memory_decompression", "VK_NV_copy_memory_indirect",
        "VK_KHR_device_fault", "VK_EXT_device_fault", "VK_EXT_device_address_binding_report",
        "VK_NV_device_diagnostic_checkpoints", "VK_NV_device_diagnostics_config"
    ];

    public static void LogSummary(VulkanDeviceCapabilities capabilities)
    {
        Debug.Vulkan(
            "[Vulkan.Device] Capability snapshot: advertisedExtensions={0}, requiredExtensions={1}, enabledExtensions={2}, enabledCapabilities=0x{3:X}, fallbacks={4}.",
            capabilities.AvailableExtensions.Count,
            capabilities.RequiredExtensions.Count,
            capabilities.EnabledExtensions.Count,
            (ulong)capabilities.EnabledCapabilities,
            capabilities.ActiveFallbacks);

        for (int i = 0; i < capabilities.RequiredExtensions.Count; i++)
        {
            string extension = capabilities.RequiredExtensions[i];
            Debug.Vulkan(
                "[Vulkan.Device] Required extension {0}: advertised={1}, enabled={2}.",
                extension,
                capabilities.AvailableExtensions.Contains(extension),
                capabilities.EnabledExtensions.Contains(extension));
        }
    }

    internal static EVulkanCapabilityState CapabilityState(
        bool available,
        bool enabled,
        bool active,
        bool explicitlyRequiredMissing = false)
    {
        if (explicitlyRequiredMissing)
            return EVulkanCapabilityState.ExplicitlyRequiredMissing;
        if (active)
            return EVulkanCapabilityState.EnabledActive;
        if (enabled)
            return EVulkanCapabilityState.EnabledUnused;
        if (available)
            return EVulkanCapabilityState.AvailableDisabled;

        return EVulkanCapabilityState.Unavailable;
    }

    internal static void LogCapability(
        string name,
        EVulkanCapabilityState state,
        string apiVersion,
        string extensionName,
        string featureBit,
        string runtimeMode,
        string properties,
        string fallbackReason)
        => Debug.Vulkan(
            "[Vulkan] Capability.{0} state={1} apiVersion={2} extension='{3}' feature='{4}' runtimeMode='{5}' properties='{6}' fallback='{7}'",
            name,
            ToCapabilityStateString(state),
            apiVersion,
            extensionName,
            featureBit,
            runtimeMode,
            properties,
            fallbackReason);

    internal static string ToCapabilityStateString(EVulkanCapabilityState state)
        => state switch
        {
            EVulkanCapabilityState.Unavailable => "unavailable",
            EVulkanCapabilityState.AvailableDisabled => "available-disabled",
            EVulkanCapabilityState.EnabledUnused => "enabled-unused",
            EVulkanCapabilityState.EnabledActive => "enabled-active",
            EVulkanCapabilityState.ExplicitlyRequiredMissing => "explicitly-required-missing",
            _ => "unavailable",
        };

    internal static string FormatMemoryHeaps(PhysicalDeviceMemoryProperties memoryProperties)
    {
        if (memoryProperties.MemoryHeapCount == 0)
            return "none";

        StringBuilder builder = new();
        for (int i = 0; i < memoryProperties.MemoryHeapCount; i++)
        {
            if (builder.Length > 0)
                builder.Append("; ");

            MemoryHeap heap = memoryProperties.MemoryHeaps[i];
            builder.Append($"heap{i}:size={heap.Size},flags={heap.Flags}");
        }

        return builder.ToString();
    }

    internal static string FormatMemoryTypes(PhysicalDeviceMemoryProperties memoryProperties)
    {
        if (memoryProperties.MemoryTypeCount == 0)
            return "none";

        StringBuilder builder = new();
        for (int i = 0; i < memoryProperties.MemoryTypeCount; i++)
        {
            if (builder.Length > 0)
                builder.Append("; ");

            MemoryType memoryType = memoryProperties.MemoryTypes[i];
            builder.Append($"type{i}:heap={memoryType.HeapIndex},flags={memoryType.PropertyFlags}");
        }

        return builder.ToString();
    }

    internal static void LogVulkanDiagnosticDeviceCapabilities(
        VulkanDeviceContext deviceContext,
        in VulkanDiagnosticCapabilitySnapshot snapshot)
    {
        Debug.Vulkan(
            "[VulkanDiag] DeviceFault requested={0} khrAvailable={1} khrEnabled={2} khrFeature={3} khrVendorBinary={4} khrReportMasked={5} khrDeviceLostOnMasked={6} khrMaxReports={7} khrCommandTable={8} extAvailable={9} extEnabled={10} extFeature={11} extVendorBinary={12} extCommandTable={13} activePath={14}",
            snapshot.RequestDeviceFault,
            snapshot.KhrDeviceFaultExtensionAvailable,
            snapshot.KhrDeviceFaultExtensionEnabled,
            snapshot.KhrDeviceFaultFeatureSupported,
            snapshot.KhrDeviceFaultVendorBinaryFeatureSupported,
            snapshot.KhrDeviceFaultReportMaskedFeatureSupported,
            snapshot.KhrDeviceFaultDeviceLostOnMaskedFeatureSupported,
            snapshot.KhrDeviceFaultMaxReportCount,
            deviceContext.DeviceFaultFacility.GetDeviceFaultReportsKhr is not null,
            snapshot.ExtDeviceFaultExtensionAvailable,
            snapshot.ExtDeviceFaultExtensionEnabled,
            snapshot.ExtDeviceFaultFeatureSupported,
            snapshot.ExtDeviceFaultVendorBinaryFeatureSupported,
            deviceContext.ExtensionFunctions.ExtDeviceFault is not null,
            deviceContext.DeviceFaultFacility.IsUsingKhrDeviceFault
                ? "KHR"
                : deviceContext.ExtensionFunctions.ExtDeviceFault is not null ? "EXT" : "unavailable");

        Debug.Vulkan(
            "[VulkanDiag] AddressBindingReport requested={0} available={1} enabled={2} feature={3}",
            snapshot.RequestDeviceAddressBindingReport,
            snapshot.DeviceAddressBindingReportExtensionAvailable,
            snapshot.DeviceAddressBindingReportExtensionEnabled,
            snapshot.DeviceAddressBindingReportFeatureSupported);
        Debug.Vulkan(
            "[VulkanDiag] NvDiagnosticCheckpoints requested={0} available={1} enabled={2} commandTable={3}",
            snapshot.RequestNvDiagnosticCheckpoints,
            snapshot.NvDiagnosticCheckpointsExtensionAvailable,
            snapshot.NvDiagnosticCheckpointsExtensionEnabled,
            deviceContext.ExtensionFunctions.NvDeviceDiagnosticCheckpoints is not null);
        Debug.Vulkan(
            "[VulkanDiag] NvDiagnosticsConfig requested={0} available={1} enabled={2} feature={3}",
            snapshot.RequestNvDiagnosticsConfig,
            snapshot.NvDiagnosticsConfigExtensionAvailable,
            snapshot.NvDiagnosticsConfigExtensionEnabled,
            snapshot.NvDiagnosticsConfigFeatureSupported);
        Debug.Vulkan(
            "[VulkanDiag] VendorCrashHooks amdIntelRuntimeDependency=none amdIntelNativeHook={0} fallbackArtifacts={1}",
            "unavailable",
            "deviceFault,addressBindingReport,validationSummary");

        if (snapshot.RequestDeviceFault &&
            !(deviceContext.DeviceFaultFacility.SupportsKhrDeviceFault || deviceContext.DeviceFaultFacility.SupportsExtDeviceFault))
            Debug.VulkanWarning("[VulkanDiag] Device-fault reports will be unavailable for this run.");
        if (snapshot.RequestNvDiagnosticCheckpoints &&
            !(deviceContext.MutableCapabilities._supportsNvDiagnosticCheckpoints &&
              deviceContext.ExtensionFunctions.NvDeviceDiagnosticCheckpoints is not null))
            Debug.VulkanWarning("[VulkanDiag] NV diagnostic checkpoints will be unavailable for this run.");
        if (snapshot.RequestNvDiagnosticsConfig && !deviceContext.MutableCapabilities._supportsNvDiagnosticsConfig)
            Debug.VulkanWarning("[VulkanDiag] NV diagnostics config will be unavailable for this run.");
    }

    internal static void ReportLayeredShadowCapabilities(
        VulkanDeviceContext deviceContext,
        in VulkanLayeredShadowCapabilityRequest request)
    {
        deviceContext.Api.GetPhysicalDeviceProperties(
            deviceContext.PhysicalDevice,
            out PhysicalDeviceProperties properties);
        int maxViewports = request.EnableMultiViewport
            ? Math.Max(1, unchecked((int)Math.Min(properties.Limits.MaxViewports, (uint)int.MaxValue)))
            : 1;
        bool geometryShader = deviceContext.MutableCapabilities._supportsGeometryShader;

        RuntimeEngine.Rendering.State.SupportsOpenGLViewportArray = request.EnableMultiViewport;
        RuntimeEngine.Rendering.State.SupportsOpenGLViewportScissorArray = request.EnableMultiViewport;
        RuntimeEngine.Rendering.State.SupportsOpenGLVertexShaderViewportIndex =
            request.EnableMultiViewport && request.EnableShaderOutputViewportIndex;
        RuntimeEngine.Rendering.State.SupportsOpenGLGeometryShaderViewportIndex =
            request.EnableMultiViewport && request.EnableShaderOutputViewportIndex && geometryShader;
        RuntimeEngine.Rendering.State.SupportsOpenGLVertexShaderLayeredRendering = request.EnableShaderOutputLayer;
        RuntimeEngine.Rendering.State.SupportsOpenGLGeometryShaderLayeredRendering =
            request.EnableShaderOutputLayer && geometryShader;
        RuntimeEngine.Rendering.State.SupportsOpenGLLayeredFramebuffers = request.EnableShaderOutputLayer;
        RuntimeEngine.Rendering.State.MaxOpenGLViewports = maxViewports;

        Debug.Vulkan(
            "[Vulkan] Layered shadow planner capabilities: multiViewport={0}, maxViewports={1}, shaderOutputViewportIndex={2}, shaderOutputLayer={3}, geometryShader={4}.",
            request.EnableMultiViewport,
            maxViewports,
            request.EnableShaderOutputViewportIndex,
            request.EnableShaderOutputLayer,
            geometryShader);
    }

    internal static void LogStartupCapabilitySnapshot(
        VulkanDeviceContext deviceContext,
        in VulkanStartupCapabilitySnapshot snapshot)
    {
        bool Supports(EVulkanDeviceCapability capability) => deviceContext.Capabilities.Supports(capability);
        VulkanDeviceMutableCapabilities mutable = deviceContext.MutableCapabilities;
        VulkanDeviceExtensionFunctions functions = deviceContext.ExtensionFunctions;
        VulkanMeshShaderCapabilitySnapshot meshShaderCapability = mutable._meshShaderCapabilitySnapshot;
        bool SupportsDynamicRendering = Supports(EVulkanDeviceCapability.DynamicRendering);
        bool SupportsSynchronization2 = Supports(EVulkanDeviceCapability.Synchronization2);
        bool SupportsMaintenance4 = Supports(EVulkanDeviceCapability.Maintenance4);
        bool SupportsBufferDeviceAddress = Supports(EVulkanDeviceCapability.BufferDeviceAddress);
        bool SupportsVulkan14 = Supports(EVulkanDeviceCapability.Vulkan14);
        bool SupportsDynamicRenderingLocalRead = Supports(EVulkanDeviceCapability.DynamicRenderingLocalRead);
        bool SupportsDynamicRenderingLocalReadStorageResources = SupportsDynamicRenderingLocalRead && mutable._supportsDynamicRenderingLocalReadStorageResources;
        bool SupportsDynamicRenderingLocalReadColorAttachments = SupportsDynamicRenderingLocalRead && mutable._supportsDynamicRenderingLocalReadColorAttachments;
        bool SupportsDynamicRenderingLocalReadDepthStencilAttachments = SupportsDynamicRenderingLocalRead && mutable._supportsDynamicRenderingLocalReadDepthStencilAttachments;
        bool SupportsDynamicRenderingLocalReadMultisampledAttachments = SupportsDynamicRenderingLocalRead && mutable._supportsDynamicRenderingLocalReadMultisampledAttachments;
        bool SupportsShaderObject = Supports(EVulkanDeviceCapability.ShaderObject);
        bool SupportsDescriptorHeap = Supports(EVulkanDeviceCapability.DescriptorHeap);
        bool SupportsNvCopyMemoryIndirect = Supports(EVulkanDeviceCapability.NvCopyMemoryIndirect) && functions.NvCopyMemoryIndirect is not null;
        bool SupportsGraphicsPipelineLibrary = Supports(EVulkanDeviceCapability.GraphicsPipelineLibrary);
        bool SupportsVulkanFragmentShadingRate = Supports(EVulkanDeviceCapability.FragmentShadingRate);
        bool SupportsVulkanFragmentShadingRateAttachment = mutable._supportsVulkanFragmentShadingRateAttachment;
        bool SupportsVulkanFragmentDensityMap = Supports(EVulkanDeviceCapability.FragmentDensityMap);
        bool SupportsVulkanFragmentDensityMapDynamic = mutable._supportsVulkanFragmentDensityMapDynamic;
        bool SupportsVulkanMeshTaskIndirectCount = Supports(EVulkanDeviceCapability.MeshShader) && mutable._supportsVulkanMeshTaskIndirectCount && meshShaderCapability.HasPortableMeshletProfile && functions.ExtMeshShader is not null;
        bool SupportsExternalMemoryWin32 = mutable._supportsExternalMemoryWin32 && functions.KhrExternalMemoryWin32 is not null;
        bool SupportsExternalSemaphoreWin32 = mutable._supportsExternalSemaphoreWin32 && functions.KhrExternalSemaphoreWin32 is not null;
        bool SupportsMemoryBudget = Supports(EVulkanDeviceCapability.MemoryBudget);
        bool SupportsMemoryPriority = Supports(EVulkanDeviceCapability.MemoryPriority);
        bool SupportsAccelerationStructure = Supports(EVulkanDeviceCapability.AccelerationStructure);
        bool SupportsRayTracingPipeline = Supports(EVulkanDeviceCapability.RayTracingPipeline);
        bool SupportsRayQuery = Supports(EVulkanDeviceCapability.RayQuery);
        bool SupportsDeviceGeneratedCommands = Supports(EVulkanDeviceCapability.DeviceGeneratedCommands);
        bool SupportsMaintenance5 = Supports(EVulkanDeviceCapability.Maintenance5);
        bool SupportsExtendedFlags = mutable._supportsExtendedFlags;
        bool SupportsDepthClipControl = Supports(EVulkanDeviceCapability.DepthClipControl);
        bool SupportsIndexTypeUint8 = Supports(EVulkanDeviceCapability.IndexTypeUint8);
        bool SupportsTransformFeedback = Supports(EVulkanDeviceCapability.TransformFeedback) && functions.ExtTransformFeedback is not null;
        bool SupportsTransformFeedbackQueries = SupportsTransformFeedback && mutable._supportsTransformFeedbackQueries;
        bool SupportsTransformFeedbackDraw = SupportsTransformFeedback && mutable._supportsTransformFeedbackDraw;
        bool SupportsTransformFeedbackGeometryStreams = SupportsTransformFeedback && mutable._supportsTransformFeedbackGeometryStreams;
        bool SupportsNvMemoryDecompression = Supports(EVulkanDeviceCapability.NvMemoryDecompression) && functions.NvMemoryDecompression is not null;
        MemoryDecompressionMethodFlagsNV NvMemoryDecompressionMethods = mutable._nvMemoryDecompressionMethods;
        ulong NvCopyMemoryIndirectSupportedQueues = mutable._nvCopyMemoryIndirectSupportedQueues;

        deviceContext.Api.GetPhysicalDeviceProperties(deviceContext.PhysicalDevice, out PhysicalDeviceProperties properties);
        deviceContext.Api.GetPhysicalDeviceMemoryProperties(deviceContext.PhysicalDevice, out PhysicalDeviceMemoryProperties memoryProperties);

        string apiVersion = VulkanDeviceContext.FormatVulkanApiVersion(properties.ApiVersion);
        var availableExtensions = new HashSet<string>(
            deviceContext.AvailableDeviceExtensions,
            StringComparer.Ordinal);
        var enabledExtensions = new HashSet<string>(
            deviceContext.EnabledDeviceExtensions,
            StringComparer.Ordinal);
        bool HasExtension(string extensionName) => availableExtensions.Contains(extensionName);
        bool HasEnabledExtension(string extensionName) => enabledExtensions.Contains(extensionName);

        EVulkanDescriptorBackend requestedDescriptorBackend = snapshot.RequestedDescriptorBackend;
        EVulkanDescriptorBackend descriptorBackend = snapshot.ActiveDescriptorBackend;
        EVulkanProgramBindingBackend programBindingBackend = snapshot.RequestedProgramBindingBackend;
        EVulkanFoveationBackend foveationBackend = snapshot.RequestedFoveationBackend;
        EVulkanRayTracingBackend rayTracingBackend = snapshot.RequestedRayTracingBackend;

        bool productionTierReady =
            SupportsDynamicRendering &&
            SupportsSynchronization2 &&
            deviceContext.MutableCapabilities._supportsTimelineSemaphores &&
            SupportsMaintenance4 &&
            snapshot.DescriptorIndexingSupported &&
            SupportsBufferDeviceAddress &&
            deviceContext.MutableCapabilities._supportsDrawIndirectCount;
        bool vulkan14OptInTierReady =
            SupportsVulkan14 &&
            SupportsDynamicRenderingLocalRead &&
            SupportsMaintenance5;
        bool vulkan14ExperimentalTierReady =
            vulkan14OptInTierReady &&
            HasExtension(VulkanDescriptorHeapExt.ExtensionName) &&
            snapshot.DescriptorHeapNativeApiAvailable &&
            SupportsShaderObject;

        Debug.Vulkan(
            "[Vulkan] Capability.Snapshot apiVersion={0} requestedTier={1} requestedDescriptorBackend={2} activeDescriptorBackend={3} requestedProgramBindingBackend={4} requestedFoveationBackend={5} requestedRayTracingBackend={6}",
            apiVersion,
            snapshot.RequestedCapabilityTier,
            requestedDescriptorBackend,
            descriptorBackend,
            programBindingBackend,
            foveationBackend,
            rayTracingBackend);

        foreach (string extensionName in ReportedModernCapabilityExtensionNames)
        {
            Debug.Vulkan(
                "[Vulkan] Capability.Extension name={0} available={1} enabled={2}",
                extensionName,
                HasExtension(extensionName),
                HasEnabledExtension(extensionName));
        }

        VulkanDeviceCapabilityReporter.LogCapability(
            "Vulkan13ProductionTier",
            VulkanDeviceCapabilityReporter.CapabilityState(true, productionTierReady, productionTierReady),
            apiVersion,
            "Vulkan 1.3",
            "dynamicRendering+sync2+timelineSemaphore+descriptorIndexing+bufferDeviceAddress+drawIndirectCount+maintenance4",
            $"renderTarget={snapshot.RequestedRenderTargetMode}->{(snapshot.UseDynamicRenderingRenderTargets ? "DynamicRendering" : "LegacyRenderPass")};sync={snapshot.RequestedSynchronizationBackend}->{snapshot.ActiveSynchronizationBackend};descriptor={descriptorBackend}",
            $"ready={productionTierReady}",
            productionTierReady ? string.Empty : "Production tier incomplete; see individual capability rows.");

        VulkanDeviceCapabilityReporter.LogCapability(
            "Vulkan14OptInTier",
            VulkanDeviceCapabilityReporter.CapabilityState(SupportsVulkan14, vulkan14OptInTierReady, false),
            apiVersion,
            "Vulkan 1.4",
            "dynamicRenderingLocalRead+maintenance5",
            snapshot.RequestedCapabilityTier.ToString(),
            $"ready={vulkan14OptInTierReady}",
            vulkan14OptInTierReady ? string.Empty : "Optional Vulkan 1.4 tier is not fully available.");

        VulkanDeviceCapabilityReporter.LogCapability(
            "Vulkan14ExperimentalTier",
            VulkanDeviceCapabilityReporter.CapabilityState(SupportsVulkan14, vulkan14ExperimentalTierReady, false),
            apiVersion,
            "Vulkan 1.4 + VK_EXT_descriptor_heap + VK_EXT_shader_object",
            "descriptorHeap+shaderObject",
            snapshot.RequestedCapabilityTier.ToString(),
            $"ready={vulkan14ExperimentalTierReady};descriptorHeapNativeApi={snapshot.DescriptorHeapNativeApiAvailable};descriptorHeapStorage={snapshot.DescriptorHeapStorageReady}",
            vulkan14ExperimentalTierReady ? string.Empty : "Experimental tier remains disabled until descriptor heap native API/storage and shader-object backend exist.");

        VulkanDeviceCapabilityReporter.LogCapability(
            "DynamicRendering",
            VulkanDeviceCapabilityReporter.CapabilityState(SupportsDynamicRendering, SupportsDynamicRendering, snapshot.UseDynamicRenderingRenderTargets),
            apiVersion,
            "VK_KHR_dynamic_rendering / Vulkan 1.3",
            "dynamicRendering",
            $"{snapshot.RequestedRenderTargetMode}->{(snapshot.UseDynamicRenderingRenderTargets ? "DynamicRendering" : "LegacyRenderPass")}",
            $"extensionEnabled={HasEnabledExtension("VK_KHR_dynamic_rendering")}",
            snapshot.UseDynamicRenderingRenderTargets ? string.Empty : "Legacy render-pass target mode selected or dynamic rendering unavailable.");

        VulkanDeviceCapabilityReporter.LogCapability(
            "DynamicRenderingLocalRead",
            VulkanDeviceCapabilityReporter.CapabilityState(
                HasExtension("VK_KHR_dynamic_rendering_local_read") || SupportsVulkan14,
                SupportsDynamicRenderingLocalRead,
                false),
            apiVersion,
            "VK_KHR_dynamic_rendering_local_read / Vulkan 1.4",
            "dynamicRenderingLocalRead",
            "OptionalPrototype",
            $"storageResources={SupportsDynamicRenderingLocalReadStorageResources};singleSampledColor={SupportsDynamicRenderingLocalReadColorAttachments};depthStencil={SupportsDynamicRenderingLocalReadDepthStencilAttachments};multisampled={SupportsDynamicRenderingLocalReadMultisampledAttachments}",
            SupportsDynamicRenderingLocalRead ? "No pass has opted into local-read barriers yet." : "Local read remains optional until Vulkan 1.4 tier is required.");

        VulkanDeviceCapabilityReporter.LogCapability(
            "Synchronization2",
            VulkanDeviceCapabilityReporter.CapabilityState(
                HasExtension("VK_KHR_synchronization2") || VulkanDeviceContext.IsVulkanApiVersionAtLeast(properties.ApiVersion, 1u, 3u),
                SupportsSynchronization2,
                snapshot.ActiveSynchronizationBackend == EVulkanSynchronizationBackend.Sync2),
            apiVersion,
            "VK_KHR_synchronization2 / Vulkan 1.3",
            "synchronization2",
            $"{snapshot.RequestedSynchronizationBackend}->{snapshot.ActiveSynchronizationBackend}",
            $"featureSupported={deviceContext.MutableCapabilities._supportsSynchronization2Feature}",
            snapshot.ActiveSynchronizationBackend == EVulkanSynchronizationBackend.Sync2 ? string.Empty : "Legacy synchronization backend selected or Sync2 unavailable.");

        VulkanDeviceCapabilityReporter.LogCapability(
            "TimelineSemaphore",
            VulkanDeviceCapabilityReporter.CapabilityState(deviceContext.MutableCapabilities._supportsTimelineSemaphores, deviceContext.MutableCapabilities._supportsTimelineSemaphores, deviceContext.MutableCapabilities._supportsTimelineSemaphores),
            apiVersion,
            "Vulkan 1.2 timelineSemaphore",
            "timelineSemaphore",
            "FramePacing",
            "required=True",
            deviceContext.MutableCapabilities._supportsTimelineSemaphores ? string.Empty : "Renderer timeline synchronization requires timeline semaphores.");

        VulkanDeviceCapabilityReporter.LogCapability(
            "DescriptorIndexing",
            VulkanDeviceCapabilityReporter.CapabilityState(
                HasExtension("VK_EXT_descriptor_indexing") || VulkanDeviceContext.IsVulkanApiVersionAtLeast(properties.ApiVersion, 1u, 2u),
                snapshot.DescriptorIndexingSupported,
                snapshot.DescriptorIndexingSupported && descriptorBackend == EVulkanDescriptorBackend.DescriptorIndexing),
            apiVersion,
            "VK_EXT_descriptor_indexing / Vulkan 1.2",
            "descriptorIndexing+runtimeDescriptorArray+partiallyBound+updateAfterBind",
            descriptorBackend.ToString(),
            $"runtimeArray={deviceContext.MutableCapabilities._supportsRuntimeDescriptorArray};partiallyBound={deviceContext.MutableCapabilities._supportsDescriptorBindingPartiallyBound};updateAfterBind={deviceContext.MutableCapabilities._supportsDescriptorBindingUpdateAfterBind};storageImageUpdateAfterBind={deviceContext.MutableCapabilities._supportsDescriptorBindingStorageImageUpdateAfterBind}",
            snapshot.DescriptorIndexingSupported ? string.Empty : "Descriptor sets remain on the non-indexed path.");

        VulkanBindlessMaterialCapability bindlessMaterialCapability = snapshot.BindlessMaterialCapability;
        VulkanDeviceCapabilityReporter.LogCapability(
            "BindlessMaterialTextures",
            VulkanDeviceCapabilityReporter.CapabilityState(
                snapshot.DescriptorIndexingSupported,
                bindlessMaterialCapability.Tier >= EVulkanBindlessMaterialCapabilityTier.DescriptorIndexingReady,
                bindlessMaterialCapability.DrawPathReady),
            apiVersion,
            "VK_EXT_descriptor_indexing / Vulkan 1.2",
            "descriptorIndexing",
            bindlessMaterialCapability.Mode.ToString(),
            $"tier={bindlessMaterialCapability.Tier};capacity={bindlessMaterialCapability.DescriptorCapacity};tableReady={bindlessMaterialCapability.GlobalDescriptorTableReady};shaderReady={bindlessMaterialCapability.ShaderReady};drawPathReady={bindlessMaterialCapability.DrawPathReady}",
            bindlessMaterialCapability.Reason);

        VulkanDeviceCapabilityReporter.LogCapability(
            "DescriptorHeap",
            VulkanDeviceCapabilityReporter.CapabilityState(HasExtension(VulkanDescriptorHeapExt.ExtensionName), SupportsDescriptorHeap, snapshot.ActiveDescriptorBackend == EVulkanDescriptorBackend.DescriptorHeap),
            apiVersion,
            "VK_EXT_descriptor_heap",
            "descriptorHeap",
            $"{requestedDescriptorBackend}->{descriptorBackend}",
            snapshot.DescriptorHeapProperties,
            HasExtension(VulkanDescriptorHeapExt.ExtensionName)
                ? snapshot.DescriptorBackendFallbackReason
                : "VK_EXT_descriptor_heap is not exposed by the selected physical device.");

        VulkanDeviceCapabilityReporter.LogCapability(
            "DescriptorBuffer",
            VulkanDeviceCapabilityReporter.CapabilityState(HasExtension("VK_EXT_descriptor_buffer"), false, false),
            apiVersion,
            "VK_EXT_descriptor_buffer",
            "descriptorBuffer",
            "NotTargetBackend",
            "deprecatedBy=VK_EXT_descriptor_heap",
            "Descriptor buffer is intentionally not the long-term modernization backend.");

        VulkanDeviceCapabilityReporter.LogCapability(
            "BufferDeviceAddress",
            VulkanDeviceCapabilityReporter.CapabilityState(
                HasExtension("VK_KHR_buffer_device_address") || VulkanDeviceContext.IsVulkanApiVersionAtLeast(properties.ApiVersion, 1u, 2u),
                SupportsBufferDeviceAddress,
                SupportsBufferDeviceAddress && (SupportsNvCopyMemoryIndirect || snapshot.EnableBindlessMaterialTable)),
            apiVersion,
            "VK_KHR_buffer_device_address / Vulkan 1.2",
            "bufferDeviceAddress",
            snapshot.ActiveGeometryFetchMode.ToString(),
            $"nvCopyMemoryIndirect={SupportsNvCopyMemoryIndirect};bindlessMaterial={snapshot.EnableBindlessMaterialTable}",
            SupportsBufferDeviceAddress ? string.Empty : "Buffer-device-address consumers remain disabled.");

        VulkanDeviceCapabilityReporter.LogCapability(
            "ShaderObject",
            VulkanDeviceCapabilityReporter.CapabilityState(HasExtension("VK_EXT_shader_object"), SupportsShaderObject, false),
            apiVersion,
            "VK_EXT_shader_object",
            "shaderObject",
            $"{programBindingBackend}->PipelineObjects",
            $"shaderBinaryVersion={deviceContext.MutableCapabilities._shaderObjectProperties.ShaderBinaryVersion}",
            SupportsShaderObject ? "Shader-object backend is not implemented yet; pipeline objects remain active." : "VK_EXT_shader_object unavailable or feature bit is false.");

        VulkanDeviceCapabilityReporter.LogCapability(
            "GraphicsPipelineLibrary",
            VulkanDeviceCapabilityReporter.CapabilityState(HasExtension("VK_EXT_graphics_pipeline_library"), SupportsGraphicsPipelineLibrary, SupportsGraphicsPipelineLibrary),
            apiVersion,
            "VK_KHR_pipeline_library + VK_EXT_graphics_pipeline_library",
            "graphicsPipelineLibrary",
            "PipelineObjects",
            $"dependencyEnabled={HasEnabledExtension("VK_KHR_pipeline_library")}",
            SupportsGraphicsPipelineLibrary ? string.Empty : "Monolithic pipeline fallback remains available.");

        VulkanDeviceCapabilityReporter.LogCapability(
            "FragmentShadingRate",
            VulkanDeviceCapabilityReporter.CapabilityState(HasExtension("VK_KHR_fragment_shading_rate"), SupportsVulkanFragmentShadingRate, false),
            apiVersion,
            "VK_KHR_fragment_shading_rate",
            "pipelineFragmentShadingRate|primitiveFragmentShadingRate|attachmentFragmentShadingRate",
            $"{foveationBackend}->Off",
            $"attachment={SupportsVulkanFragmentShadingRateAttachment}",
            SupportsVulkanFragmentShadingRate ? "VRS/foveation backend is not implemented yet." : "Fragment shading rate unavailable.");

        VulkanDeviceCapabilityReporter.LogCapability(
            "FragmentDensityMap",
            VulkanDeviceCapabilityReporter.CapabilityState(HasExtension("VK_EXT_fragment_density_map"), SupportsVulkanFragmentDensityMap, false),
            apiVersion,
            "VK_EXT_fragment_density_map",
            "fragmentDensityMap",
            $"{foveationBackend}->Off",
            $"dynamic={SupportsVulkanFragmentDensityMapDynamic}",
            SupportsVulkanFragmentDensityMap ? "Density-map foveation backend is not implemented yet." : "Fragment density map unavailable.");

        VulkanDeviceCapabilityReporter.LogCapability(
            "Multiview",
            VulkanDeviceCapabilityReporter.CapabilityState(
                HasExtension("VK_KHR_multiview") || VulkanDeviceContext.IsVulkanApiVersionAtLeast(properties.ApiVersion, 1u, 1u),
                snapshot.HasVulkanMultiView,
                snapshot.HasVulkanMultiView),
            apiVersion,
            "VK_KHR_multiview / Vulkan 1.1",
            "multiview",
            "StereoTargets",
            $"enabled={snapshot.HasVulkanMultiView}",
            snapshot.HasVulkanMultiView ? string.Empty : "Stereo single-pass multiview remains disabled.");

        VulkanDeviceCapabilityReporter.LogCapability(
            "MeshShaderEXT",
            VulkanDeviceCapabilityReporter.CapabilityState(HasExtension("VK_EXT_mesh_shader"), SupportsVulkanMeshTaskIndirectCount, false),
            apiVersion,
            "VK_EXT_mesh_shader",
            "taskShader+meshShader",
            "MeshletDispatch",
            $"advertised={meshShaderCapability.ExtensionAdvertised};requested={meshShaderCapability.ExtensionRequested};enabled={meshShaderCapability.ExtensionEnabled};taskAdvertised={meshShaderCapability.TaskShaderAdvertised};meshAdvertised={meshShaderCapability.MeshShaderAdvertised};taskEnabled={meshShaderCapability.TaskShaderEnabled};meshEnabled={meshShaderCapability.MeshShaderEnabled};queriesAdvertised={meshShaderCapability.MeshShaderQueriesAdvertised};queriesEnabled={meshShaderCapability.MeshShaderQueriesEnabled};commandsLoaded={meshShaderCapability.CommandTableLoaded};taskInvocations={meshShaderCapability.Properties.MaxTaskWorkGroupInvocations};taskPayload={meshShaderCapability.Properties.MaxTaskPayloadSize};taskShared={meshShaderCapability.Properties.MaxTaskSharedMemorySize};taskPayloadShared={meshShaderCapability.Properties.MaxTaskPayloadAndSharedMemorySize};meshInvocations={meshShaderCapability.Properties.MaxMeshWorkGroupInvocations};outputVertices={meshShaderCapability.Properties.MaxMeshOutputVertices};outputPrimitives={meshShaderCapability.Properties.MaxMeshOutputPrimitives};meshOutputMemory={meshShaderCapability.Properties.MaxMeshOutputMemorySize};meshPayloadOutputMemory={meshShaderCapability.Properties.MaxMeshPayloadAndOutputMemorySize};portableProfile={meshShaderCapability.HasPortableMeshletProfile}",
            SupportsVulkanMeshTaskIndirectCount ? "Mesh/task shader capability loaded; production meshlet dispatch remains profile-gated." : meshShaderCapability.GetDispatchFailureReason());

        VulkanDeviceCapabilityReporter.LogCapability(
            "DrawIndirectCount",
            VulkanDeviceCapabilityReporter.CapabilityState(
                HasExtension("VK_KHR_draw_indirect_count") || VulkanDeviceContext.IsVulkanApiVersionAtLeast(properties.ApiVersion, 1u, 2u),
                deviceContext.MutableCapabilities._supportsDrawIndirectCount,
                deviceContext.MutableCapabilities._supportsDrawIndirectCount),
            apiVersion,
            "VK_KHR_draw_indirect_count / Vulkan 1.2",
            "drawIndirectCount",
            "GpuDrivenIndirect",
            $"extensionLoaded={deviceContext.ExtensionFunctions.KhrDrawIndirectCount is not null}",
            deviceContext.MutableCapabilities._supportsDrawIndirectCount ? string.Empty : "Multi-draw indirect count falls back to non-count indirect path.");

        VulkanDeviceCapabilityReporter.LogCapability(
            "ExternalMemorySemaphore",
            VulkanDeviceCapabilityReporter.CapabilityState(
                HasExtension("VK_KHR_external_memory") || HasExtension("VK_KHR_external_semaphore"),
                SupportsExternalMemoryWin32 && SupportsExternalSemaphoreWin32,
                SupportsExternalMemoryWin32 && SupportsExternalSemaphoreWin32),
            apiVersion,
            "VK_KHR_external_memory + VK_KHR_external_semaphore + Win32 variants",
            "externalMemoryWin32+externalSemaphoreWin32",
            "Interop",
            $"memoryWin32={SupportsExternalMemoryWin32};semaphoreWin32={SupportsExternalSemaphoreWin32}",
            SupportsExternalMemoryWin32 && SupportsExternalSemaphoreWin32 ? string.Empty : "Interop/upscale/OBS paths requiring external sharing remain disabled.");

        VulkanDeviceCapabilityReporter.LogCapability(
            "MemoryBudget",
            VulkanDeviceCapabilityReporter.CapabilityState(HasExtension("VK_EXT_memory_budget"), SupportsMemoryBudget, false),
            apiVersion,
            "VK_EXT_memory_budget",
            "memoryBudget",
            "AllocatorDiagnostics",
            "heapBudget=reported-when-enabled",
            SupportsMemoryBudget ? "Allocator residency policy has not consumed memory budget yet." : "Memory budget extension unavailable or disabled.");

        VulkanDeviceCapabilityReporter.LogCapability(
            "MemoryPriority",
            VulkanDeviceCapabilityReporter.CapabilityState(HasExtension("VK_EXT_memory_priority"), SupportsMemoryPriority, false),
            apiVersion,
            "VK_EXT_memory_priority",
            "memoryPriority",
            "AllocatorDiagnostics",
            $"feature={SupportsMemoryPriority}",
            SupportsMemoryPriority ? "Allocator priority policy has not consumed memory priority yet." : "Memory priority extension unavailable or feature bit is false.");

        VulkanDeviceCapabilityReporter.LogCapability(
            "TransientLazyAttachments",
            VulkanDeviceCapabilityReporter.CapabilityState(true, deviceContext.MutableCapabilities.SupportsLazyAllocation, deviceContext.MutableCapabilities.SupportsLazyAllocation),
            apiVersion,
            "MemoryPropertyFlags.LazilyAllocatedBit",
            "lazilyAllocatedMemoryType",
            "TransientAttachmentPolicy",
            $"lazyAlloc={deviceContext.MutableCapabilities.SupportsLazyAllocation}",
            deviceContext.MutableCapabilities.SupportsLazyAllocation ? string.Empty : "Transient images fall back to regular device-local memory.");

        VulkanDeviceCapabilityReporter.LogCapability(
            "RayTracingPipeline",
            VulkanDeviceCapabilityReporter.CapabilityState(
                HasExtension("VK_KHR_ray_tracing_pipeline"),
                SupportsAccelerationStructure && SupportsRayTracingPipeline,
                false),
            apiVersion,
            "VK_KHR_acceleration_structure + VK_KHR_ray_tracing_pipeline + VK_KHR_deferred_host_operations",
            "accelerationStructure+rayTracingPipeline",
            $"{rayTracingBackend}->Off",
            $"accelerationStructure={SupportsAccelerationStructure};rayTracingPipeline={SupportsRayTracingPipeline}",
            SupportsAccelerationStructure && SupportsRayTracingPipeline ? "Vulkan ray tracing backend is not implemented yet." : "KHR ray tracing pipeline requirements are incomplete.");

        VulkanDeviceCapabilityReporter.LogCapability(
            "RayQuery",
            VulkanDeviceCapabilityReporter.CapabilityState(HasExtension("VK_KHR_ray_query"), SupportsAccelerationStructure && SupportsRayQuery, false),
            apiVersion,
            "VK_KHR_ray_query",
            "rayQuery",
            $"{rayTracingBackend}->Off",
            $"accelerationStructure={SupportsAccelerationStructure};rayQuery={SupportsRayQuery}",
            SupportsAccelerationStructure && SupportsRayQuery ? "Vulkan ray-query backend is not implemented yet." : "Ray query requirements are incomplete.");

        VulkanDeviceCapabilityReporter.LogCapability(
            "DeviceGeneratedCommands",
            VulkanDeviceCapabilityReporter.CapabilityState(HasExtension("VK_EXT_device_generated_commands"), SupportsDeviceGeneratedCommands, false),
            apiVersion,
            "VK_EXT_device_generated_commands",
            "deviceGeneratedCommands",
            "Deferred",
            $"feature={SupportsDeviceGeneratedCommands}",
            SupportsDeviceGeneratedCommands ? "Deferred until descriptor heap/shader object architecture is stable." : "Device-generated commands unavailable.");

        VulkanDeviceCapabilityReporter.LogCapability(
            "Maintenance4",
            VulkanDeviceCapabilityReporter.CapabilityState(
                HasExtension("VK_KHR_maintenance4") || VulkanDeviceContext.IsVulkanApiVersionAtLeast(properties.ApiVersion, 1u, 3u),
                SupportsMaintenance4,
                SupportsMaintenance4),
            apiVersion,
            "VK_KHR_maintenance4 / Vulkan 1.3",
            "maintenance4",
            "ProductionTier",
            $"enabled={SupportsMaintenance4}",
            SupportsMaintenance4 ? string.Empty : "Maintenance4 unavailable.");

        VulkanDeviceCapabilityReporter.LogCapability(
            "Maintenance5",
            VulkanDeviceCapabilityReporter.CapabilityState(
                HasExtension("VK_KHR_maintenance5") || SupportsVulkan14,
                SupportsMaintenance5,
                false),
            apiVersion,
            "VK_KHR_maintenance5 / Vulkan 1.4",
            "maintenance5",
            "DescriptorHeapDependency",
            $"enabled={SupportsMaintenance5}",
            SupportsMaintenance5 ? "Available for descriptor heap dependency checks." : "Maintenance5 unavailable.");

        VulkanDeviceCapabilityReporter.LogCapability(
            "ExtendedFlags",
            VulkanDeviceCapabilityReporter.CapabilityState(HasExtension("VK_KHR_extended_flags"), SupportsExtendedFlags, false),
            apiVersion,
            "VK_KHR_extended_flags",
            "extendedFlags",
            "DescriptorHeapDependency",
            $"enabled={SupportsExtendedFlags}",
            SupportsExtendedFlags ? "Available for descriptor heap dependency checks." : "Extended flags unavailable.");

        VulkanDeviceCapabilityReporter.LogCapability(
            "ShaderUntypedPointers",
            VulkanDeviceCapabilityReporter.CapabilityState(HasExtension(VulkanDescriptorHeapExt.ShaderUntypedPointersExtensionName), snapshot.DescriptorHeapShaderUntypedPointersAvailable, false),
            apiVersion,
            VulkanDescriptorHeapExt.ShaderUntypedPointersExtensionName,
            "shaderUntypedPointers",
            "DescriptorHeapDependency",
            $"available={snapshot.DescriptorHeapShaderUntypedPointersAvailable}",
            snapshot.DescriptorHeapShaderUntypedPointersAvailable
                ? "Descriptor heap dependency is present; legacy set/binding mappings do not require enabling it."
                : "Descriptor heap requires shader untyped pointers support.");

        VulkanDeviceCapabilityReporter.LogCapability(
            "DepthClipViewportLayer",
            VulkanDeviceCapabilityReporter.CapabilityState(
                HasExtension(VulkanDepthClipControlExt.ExtensionName) || VulkanDeviceContext.IsVulkanApiVersionAtLeast(properties.ApiVersion, 1u, 2u),
                SupportsDepthClipControl || snapshot.SupportsVertexShaderLayeredRendering,
                SupportsDepthClipControl || snapshot.SupportsVertexShaderLayeredRendering),
            apiVersion,
            "VK_EXT_depth_clip_control + VK_EXT_shader_viewport_index_layer",
            "depthClipControl+shaderOutputViewportIndex+shaderOutputLayer",
            "LayeredShadowPlanning",
            $"depthClip={SupportsDepthClipControl};viewportLayer={snapshot.SupportsVertexShaderLayeredRendering}",
            SupportsDepthClipControl ? string.Empty : "Depth clip control unavailable; clip-space/layered paths use fallbacks.");

        VulkanDeviceCapabilityReporter.LogCapability(
            "IndexTypeUint8",
            VulkanDeviceCapabilityReporter.CapabilityState(
                HasExtension("VK_EXT_index_type_uint8") || HasExtension("VK_KHR_index_type_uint8") || SupportsVulkan14,
                SupportsIndexTypeUint8,
                false),
            apiVersion,
            "VK_EXT_index_type_uint8 / VK_KHR_index_type_uint8 / Vulkan 1.4",
            "indexTypeUint8",
            "IndexBuffers",
            $"enabled={SupportsIndexTypeUint8}",
            SupportsIndexTypeUint8 ? "Byte-sized index buffers are allowed." : "Byte-sized index buffers are skipped.");

        VulkanDeviceCapabilityReporter.LogCapability(
            "TransformFeedback",
            VulkanDeviceCapabilityReporter.CapabilityState(HasExtension("VK_EXT_transform_feedback"), SupportsTransformFeedback, SupportsTransformFeedback),
            apiVersion,
            "VK_EXT_transform_feedback",
            "transformFeedback",
            "LegacyParity",
            $"queries={SupportsTransformFeedbackQueries};draw={SupportsTransformFeedbackDraw};geometryStreams={SupportsTransformFeedbackGeometryStreams}",
            SupportsTransformFeedback ? string.Empty : "Transform feedback unavailable.");

        VulkanDeviceCapabilityReporter.LogCapability(
            "NvidiaDataMovement",
            VulkanDeviceCapabilityReporter.CapabilityState(
                HasExtension("VK_NV_memory_decompression") || HasExtension("VK_NV_copy_memory_indirect"),
                SupportsNvMemoryDecompression || SupportsNvCopyMemoryIndirect,
                SupportsNvMemoryDecompression || SupportsNvCopyMemoryIndirect),
            apiVersion,
            "VK_NV_memory_decompression + VK_NV_copy_memory_indirect",
            "memoryDecompression+copyMemoryIndirect",
            "RtxIo",
            $"decompression={SupportsNvMemoryDecompression};copyIndirect={SupportsNvCopyMemoryIndirect};methods=0x{NvMemoryDecompressionMethods:X};copyQueues=0x{NvCopyMemoryIndirectSupportedQueues:X}",
            SupportsNvMemoryDecompression || SupportsNvCopyMemoryIndirect ? string.Empty : "NVIDIA accelerated data movement unavailable or disabled.");

        Debug.Vulkan(
            "[Vulkan] Capability.MaxMemoryAllocationCount status=Required supported=True value={0}",
            properties.Limits.MaxMemoryAllocationCount);

        Debug.Vulkan(
            "[Vulkan] Capability.MemoryHeaps status=Required supported=True {0}",
            VulkanDeviceCapabilityReporter.FormatMemoryHeaps(memoryProperties));

        Debug.Vulkan(
            "[Vulkan] Capability.MemoryTypes status=Required supported=True {0}",
            VulkanDeviceCapabilityReporter.FormatMemoryTypes(memoryProperties));
    }
}
