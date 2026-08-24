using Silk.NET.Core.Native;
using Silk.NET.Core;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Vulkan.Extensions.NV;
using System.Runtime.CompilerServices;
using System.Reflection;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Rendering.DLSS;
using XREngine.Rendering.Vulkan.DeviceBootstrap;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

internal sealed unsafe partial class VulkanDeviceContext
{
    private const string ExtDeviceFaultExtensionName = "VK_EXT_device_fault";
    private const string KhrDeviceFaultExtensionName = "VK_KHR_device_fault";
    private const string ExtDeviceAddressBindingReportExtensionName = "VK_EXT_device_address_binding_report";
    private const string NvDeviceDiagnosticCheckpointsExtensionName = "VK_NV_device_diagnostic_checkpoints";
    private const string NvDeviceDiagnosticsConfigExtensionName = "VK_NV_device_diagnostics_config";
    private const string SwapchainMaintenance1ExtensionName = "VK_EXT_swapchain_maintenance1";
    internal static readonly string[] DefaultOptionalDeviceExtensions =
    [
        "VK_KHR_multiview",
        "VK_KHR_external_memory",
        "VK_KHR_external_semaphore",
        "VK_KHR_external_memory_win32",
        "VK_KHR_external_semaphore_win32",
        "VK_KHR_draw_indirect_count",
        "VK_KHR_synchronization2",
        "VK_KHR_shader_draw_parameters",
        "VK_EXT_shader_viewport_index_layer",
        "VK_EXT_index_type_uint8",
        "VK_EXT_descriptor_indexing",
        VulkanDescriptorHeapExt.ExtensionName,
        VulkanDescriptorHeapExt.ShaderUntypedPointersExtensionName,
        "VK_KHR_buffer_device_address",
        "VK_KHR_dynamic_rendering",
        "VK_KHR_dynamic_rendering_local_read",
        "VK_KHR_maintenance4",
        "VK_KHR_maintenance5",
        "VK_KHR_extended_flags",
        VulkanDepthClipControlExt.ExtensionName,
        "VK_KHR_pipeline_library",
        "VK_EXT_graphics_pipeline_library",
        "VK_EXT_pipeline_creation_cache_control",
        ExtTransformFeedback.ExtensionName,
        "VK_EXT_primitives_generated_query",
        "VK_KHR_fragment_shading_rate",
        "VK_EXT_fragment_density_map",
        "VK_EXT_mesh_shader",
        "VK_EXT_shader_object",
        "VK_EXT_memory_budget",
        "VK_EXT_memory_priority",
        SwapchainMaintenance1ExtensionName,
        "VK_NV_memory_decompression",
        "VK_NV_copy_memory_indirect",
    ];

    /// <summary>
    /// Creates a logical device interface to the physical device with specific
    /// queue families and extensions.
    /// </summary>
    /// <remarks>
    /// The logical device is the primary interface for an application to the GPU.
    /// This method:
    /// 1. Identifies required queue families (graphics and presentation)
    /// 2. Sets up queue creation information
    /// 3. Configures device features
    /// 4. Enables required device extensions
    /// 5. Enables validation layers if needed
    /// 6. Creates the device and obtains queue handles
    /// </remarks>
    internal VulkanLogicalDeviceBootstrapResult BootstrapLogicalDevice(
        in VulkanLogicalDeviceBootstrapRequest request)
    {
        VulkanPhysicalDeviceCapabilitySnapshot queriedCapabilities =
            PhysicalDeviceCapabilities ??
            VulkanDeviceCapabilityQuery.Query(Api, PhysicalDevice);

        VulkanLogicalDeviceBootstrapResult result =
            CreateConfiguredLogicalDevice(request, queriedCapabilities);
        PublishLogicalDeviceCapabilities(request.Extensions);
        VulkanDeviceCapabilityReporter.LogSummary(Capabilities);
        return result;
    }

    /// <summary>
    /// Applies engine enablement policy to a previously queried physical-device
    /// snapshot and creates the logical device.
    /// </summary>
    private VulkanLogicalDeviceBootstrapResult CreateConfiguredLogicalDevice(
        in VulkanLogicalDeviceBootstrapRequest request,
        VulkanPhysicalDeviceCapabilitySnapshot queriedCapabilities)
    {
        VulkanLogicalDeviceOutputPolicyState _outputRuntime = new(request.Output, request.Streamline);
        VulkanLogicalDeviceOutputPolicyState OutputRuntime = _outputRuntime;
        VulkanLogicalDeviceResourcePublicationBuilder ResourceRuntime = new(this, request.FeaturePolicy);
        VulkanLogicalDeviceDiagnosticPolicyState _frameTelemetry = new(request.Diagnostics);
        VulkanDeviceContext _deviceContext = this;

        void ResolveStreamlineVulkanRequirements(bool includeDlss, bool includeFrameGeneration)
            => _outputRuntime.UsePrecomputedRequirements(includeDlss, includeFrameGeneration);

        // Get queue family indices required for rendering and presentation
        var indices = _deviceContext.QueueFamilies;

        QueueFamilyProperties[] queueFamilies = queriedCapabilities.QueueFamilyArray;

        uint graphicsFamilyQueueCount = queueFamilies[indices.GraphicsFamilyIndex!.Value].QueueCount;
        uint engineGraphicsQueueCount = Math.Min(2u, graphicsFamilyQueueCount);
        bool supportsMultipleGraphicsQueues = engineGraphicsQueueCount > 1;

        uint graphicsFamily = indices.GraphicsFamilyIndex.Value;
        uint computeFamily = indices.ComputeFamilyIndex ?? graphicsFamily;
        uint transferFamily = indices.TransferFamilyIndex ?? computeFamily;
        uint? presentFamily = indices.PresentFamilyIndex;
        if (OutputRuntime.TargetPolicy.RequiresPresentQueue && !presentFamily.HasValue)
            throw new InvalidOperationException("The selected Vulkan target requires a presentation queue family.");
        Dictionary<uint, uint> requestedQueueCounts = [];

        static void RequireEngineQueues(Dictionary<uint, uint> counts, uint family, uint count)
        {
            if (!counts.TryGetValue(family, out uint existing) || existing < count)
                counts[family] = count;
        }

        RequireEngineQueues(requestedQueueCounts, graphicsFamily, engineGraphicsQueueCount);
        if (presentFamily.HasValue)
            RequireEngineQueues(requestedQueueCounts, presentFamily.Value, 1);
        RequireEngineQueues(requestedQueueCounts, computeFamily, 1);
        RequireEngineQueues(requestedQueueCounts, transferFamily, 1);


        bool dlssExplicitlyRequested = request.Streamline.DlssExplicitlyRequested;
        bool frameGenerationExplicitlyRequested = request.Streamline.FrameGenerationExplicitlyRequested;

        bool CanProvisionStreamlineQueues(out string failureReason)
        {
            Dictionary<uint, uint> candidateQueueCounts = new(requestedQueueCounts);
            try
            {
                AppendRequiredQueues(
                    candidateQueueCounts,
                    queueFamilies,
                    graphicsFamily,
                    _outputRuntime._streamlineQueueRequirements.GraphicsQueues,
                    "graphics");
                AppendRequiredQueues(
                    candidateQueueCounts,
                    queueFamilies,
                    computeFamily,
                    _outputRuntime._streamlineQueueRequirements.ComputeQueues,
                    "compute");
                if (_outputRuntime._streamlineQueueRequirements.OpticalFlowQueues > 0)
                {
                    uint opticalFlowFamily = FindOpticalFlowQueueFamily(queueFamilies);
                    AppendRequiredQueues(
                        candidateQueueCounts,
                        queueFamilies,
                        opticalFlowFamily,
                        _outputRuntime._streamlineQueueRequirements.OpticalFlowQueues,
                        "optical-flow");
                }

                failureReason = string.Empty;
                return true;
            }
            catch (NotSupportedException ex)
            {
                failureReason = ex.Message;
                return false;
            }
        }

        while (!CanProvisionStreamlineQueues(out string streamlineQueueFailure))
        {
            if (_outputRuntime._streamlineFrameGenerationProvisioned && !frameGenerationExplicitlyRequested)
            {
                Debug.RenderingWarning(
                    "[Vulkan] Optional DLSS-G runtime-toggle provisioning disabled because the selected device cannot satisfy its queue requirements. Restart the renderer on a compatible device to enable the live toggle. Reason={0}",
                    streamlineQueueFailure);
                ResolveStreamlineVulkanRequirements(_outputRuntime._streamlineDlssProvisioned, includeFrameGeneration: false);
                continue;
            }

            if (_outputRuntime._streamlineDlssProvisioned && !dlssExplicitlyRequested)
            {
                Debug.RenderingWarning(
                    "[Vulkan] Optional DLSS runtime-toggle provisioning disabled because the selected device cannot satisfy its queue requirements. Restart the renderer on a compatible device to enable the live toggle. Reason={0}",
                    streamlineQueueFailure);
                ResolveStreamlineVulkanRequirements(includeDlss: false, includeFrameGeneration: false);
                continue;
            }

            throw new NotSupportedException(streamlineQueueFailure);
        }
        _outputRuntime._streamlineGraphicsQueueFamily = graphicsFamily;
        _outputRuntime._streamlineComputeQueueFamily = computeFamily;
        _outputRuntime._streamlineOpticalFlowQueueFamily = 0;
        _outputRuntime._streamlineGraphicsQueueIndex = AppendRequiredQueues(
            requestedQueueCounts,
            queueFamilies,
            graphicsFamily,
            _outputRuntime._streamlineQueueRequirements.GraphicsQueues,
            "graphics");
        _outputRuntime._streamlineComputeQueueIndex = AppendRequiredQueues(
            requestedQueueCounts,
            queueFamilies,
            computeFamily,
            _outputRuntime._streamlineQueueRequirements.ComputeQueues,
            "compute");

        if (_outputRuntime._streamlineQueueRequirements.OpticalFlowQueues > 0)
        {
            _outputRuntime._streamlineOpticalFlowQueueFamily = FindOpticalFlowQueueFamily(queueFamilies);
            _outputRuntime._streamlineOpticalFlowQueueIndex = AppendRequiredQueues(
                requestedQueueCounts,
                queueFamilies,
                _outputRuntime._streamlineOpticalFlowQueueFamily,
                _outputRuntime._streamlineQueueRequirements.OpticalFlowQueues,
                "optical-flow");
        }
        else
        {
            _outputRuntime._streamlineOpticalFlowQueueIndex = 0;
        }

        uint[] uniqueQueueFamilies = [.. requestedQueueCounts.Keys];

        // Allocate memory for queue create infos
        using var mem = GlobalMemory.Allocate(uniqueQueueFamilies.Length * sizeof(DeviceQueueCreateInfo));
        var queueCreateInfos = (DeviceQueueCreateInfo*)Unsafe.AsPointer(ref mem.GetPinnableReference());

        // Configure queue priorities (1.0 = highest priority)
        int maxRequestedQueueCount = checked((int)requestedQueueCounts.Values.Max());
        using var priorityMemory = GlobalMemory.Allocate(checked(maxRequestedQueueCount * sizeof(float)));
        float* queuePriorities = (float*)Unsafe.AsPointer(ref priorityMemory.GetPinnableReference());
        for (int queueIndex = 0; queueIndex < maxRequestedQueueCount; queueIndex++)
            queuePriorities[queueIndex] = 1.0f;

        // Set up creation info for each queue family
        for (int i = 0; i < uniqueQueueFamilies.Length; i++)
        {
            uint queueFamilyIndex = uniqueQueueFamilies[i];

            queueCreateInfos[i] = new()
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = queueFamilyIndex,
                QueueCount = requestedQueueCounts[queueFamilyIndex],
                PQueuePriorities = queuePriorities
            };
        }

        // Specify device features to enable (none specifically enabled here)
        PhysicalDeviceFeatures supportedFeatures = queriedCapabilities.CoreFeatures;

        PhysicalDeviceFeatures deviceFeatures = new();
        ResourceRuntime.Queries.OcclusionPreciseAdvertised = supportedFeatures.OcclusionQueryPrecise;
        ResourceRuntime.Queries.OcclusionPreciseEnabled = supportedFeatures.OcclusionQueryPrecise;
        deviceFeatures.OcclusionQueryPrecise = ResourceRuntime.Queries.OcclusionPreciseEnabled;
        ResourceRuntime.Queries.PipelineStatisticsAdvertised = supportedFeatures.PipelineStatisticsQuery;
        ResourceRuntime.Queries.PipelineStatisticsEnabled = supportedFeatures.PipelineStatisticsQuery;
        deviceFeatures.PipelineStatisticsQuery = ResourceRuntime.Queries.PipelineStatisticsEnabled;
        ResourceRuntime.Queries.InheritedQueriesAdvertised = supportedFeatures.InheritedQueries;
        ResourceRuntime.Queries.InheritedQueriesEnabled = supportedFeatures.InheritedQueries;
        deviceFeatures.InheritedQueries = ResourceRuntime.Queries.InheritedQueriesEnabled;
        if (supportedFeatures.RobustBufferAccess)
            deviceFeatures.RobustBufferAccess = Vk.True;

        if (supportedFeatures.SamplerAnisotropy)
        {
            deviceFeatures.SamplerAnisotropy = Vk.True;
            _deviceContext.MutableCapabilities._supportsAnisotropy = true;
        }

        if (supportedFeatures.FragmentStoresAndAtomics)
        {
            deviceFeatures.FragmentStoresAndAtomics = Vk.True;
            _deviceContext.MutableCapabilities._supportsFragmentStoresAndAtomics = true;
        }

        if (supportedFeatures.VertexPipelineStoresAndAtomics)
        {
            deviceFeatures.VertexPipelineStoresAndAtomics = Vk.True;
            _deviceContext.MutableCapabilities._supportsVertexPipelineStoresAndAtomics = true;
        }

        if (supportedFeatures.GeometryShader)
        {
            deviceFeatures.GeometryShader = Vk.True;
            _deviceContext.MutableCapabilities._supportsGeometryShader = true;
        }

        bool enableMultiViewport = supportedFeatures.MultiViewport;
        if (enableMultiViewport)
            deviceFeatures.MultiViewport = Vk.True;

        if (supportedFeatures.SampleRateShading)
            deviceFeatures.SampleRateShading = Vk.True;

        if (supportedFeatures.IndependentBlend)
            deviceFeatures.IndependentBlend = Vk.True;

        if (supportedFeatures.MultiDrawIndirect)
        {
            deviceFeatures.MultiDrawIndirect = Vk.True;
            _deviceContext.MutableCapabilities._supportsMultiDrawIndirect = true;
        }

        if (supportedFeatures.DrawIndirectFirstInstance)
        {
            deviceFeatures.DrawIndirectFirstInstance = Vk.True;
            _deviceContext.MutableCapabilities._supportsDrawIndirectFirstInstance = true;
        }

        _deviceContext.QueryVulkan12Capabilities(
            out PhysicalDeviceVulkan12Features supportedVulkan12Features,
            out bool vulkan12PromotedToCore);

        PhysicalDeviceProperties physicalDeviceProperties = queriedCapabilities.Properties;
        bool vulkan13PromotedToCore = VulkanDeviceContext.IsVulkanApiVersionAtLeast(physicalDeviceProperties.ApiVersion, 1u, 3u);
        bool vulkan14PromotedToCore = VulkanDeviceContext.IsVulkanApiVersionAtLeast(physicalDeviceProperties.ApiVersion, 1u, 4u);
        PhysicalDevicePrivateDataFeatures supportedPrivateDataFeatures = new()
        {
            SType = StructureType.PhysicalDevicePrivateDataFeatures,
            PNext = null,
        };
        if (vulkan13PromotedToCore)
        {
            PhysicalDeviceFeatures2 privateDataFeatures2 = new()
            {
                SType = StructureType.PhysicalDeviceFeatures2,
                PNext = &supportedPrivateDataFeatures,
            };
            Api.GetPhysicalDeviceFeatures2(_deviceContext.PhysicalDevice, &privateDataFeatures2);
        }
        bool enablePrivateDataFeature =
            vulkan13PromotedToCore &&
            supportedPrivateDataFeatures.PrivateData;

        var availableExtensionSet = new HashSet<string>(
            _deviceContext.AvailableDeviceExtensions,
            StringComparer.Ordinal);
        bool meshShaderExtensionAdvertised = availableExtensionSet.Contains(ExtMeshShader.ExtensionName);
        bool meshShaderExtensionRequested = _deviceContext.Configuration.OptionalDeviceExtensions.Contains(
            ExtMeshShader.ExtensionName,
            StringComparer.Ordinal);

        // Build the list of extensions to enable (required + supported optional)
        var extensionsToEnable = new List<string>(
            _deviceContext.Configuration.RequiredDeviceExtensions);
        foreach (string requiredStreamlineExtension in _outputRuntime._streamlineRequiredDeviceExtensions)
        {
            if (!availableExtensionSet.Contains(requiredStreamlineExtension))
            {
                throw new NotSupportedException(
                    $"Streamline requires Vulkan device extension '{requiredStreamlineExtension}', but the selected physical device does not expose it.");
            }

            if (!extensionsToEnable.Contains(requiredStreamlineExtension, StringComparer.Ordinal))
                extensionsToEnable.Add(requiredStreamlineExtension);
        }

        var openXrRequirements = OpenXRAPI.GetRequestedVulkanRuntimeRequirements();
        foreach (string requiredOpenXrExtension in openXrRequirements.DeviceExtensions)
        {
            if (string.IsNullOrWhiteSpace(requiredOpenXrExtension))
                continue;

            if (!availableExtensionSet.Contains(requiredOpenXrExtension))
            {
                throw new NotSupportedException(
                    $"The active OpenXR runtime requires Vulkan device extension '{requiredOpenXrExtension}', " +
                    "but the selected Vulkan physical device does not expose it.");
            }

            if (!extensionsToEnable.Contains(requiredOpenXrExtension, StringComparer.Ordinal))
            {
                extensionsToEnable.Add(requiredOpenXrExtension);
                Debug.Vulkan($"[OpenXR] Enabling required Vulkan device extension: {requiredOpenXrExtension}");
            }
        }

        void AddDiagnosticDeviceExtensionIfRequested(string extensionName, bool requested)
        {
            if (!requested)
                return;

            if (availableExtensionSet.Contains(extensionName))
            {
                if (!extensionsToEnable.Contains(extensionName, StringComparer.Ordinal))
                {
                    extensionsToEnable.Add(extensionName);
                    Debug.Vulkan("[VulkanDiag] Enabling requested diagnostic device extension: {0}", extensionName);
                }
            }
            else
            {
                Debug.VulkanWarning("[VulkanDiag] Requested diagnostic device extension is unsupported: {0}", extensionName);
            }
        }

        if (_frameTelemetry._diagnosticOptions.RequestDeviceFault && availableExtensionSet.Contains(KhrDeviceFaultExtensionName))
        {
            Debug.Vulkan(
                "[VulkanDiag] {0} is exposed; preferring local KHR device-fault shim with {1} compatibility fallback when available.",
                KhrDeviceFaultExtensionName,
                ExtDeviceFaultExtensionName);
        }

        AddDiagnosticDeviceExtensionIfRequested(KhrDeviceFaultExtensionName, _frameTelemetry._diagnosticOptions.RequestDeviceFault);
        AddDiagnosticDeviceExtensionIfRequested(ExtDeviceFaultExtensionName, _frameTelemetry._diagnosticOptions.RequestDeviceFault);
        AddDiagnosticDeviceExtensionIfRequested(ExtDeviceAddressBindingReportExtensionName, _frameTelemetry._diagnosticOptions.RequestDeviceAddressBindingReport);
        AddDiagnosticDeviceExtensionIfRequested(NvDeviceDiagnosticCheckpointsExtensionName, _frameTelemetry._diagnosticOptions.RequestNvDiagnosticCheckpoints);
        AddDiagnosticDeviceExtensionIfRequested(NvDeviceDiagnosticsConfigExtensionName, _frameTelemetry._diagnosticOptions.RequestNvDiagnosticsConfig);

        foreach (string optionalExt in _deviceContext.Configuration.OptionalDeviceExtensions)
        {
            if (optionalExt == "VK_EXT_graphics_pipeline_library" &&
                !availableExtensionSet.Contains("VK_KHR_pipeline_library"))
            {
                Debug.VulkanWarning(
                    "[Vulkan] Optional extension {0} skipped because required dependency VK_KHR_pipeline_library is unavailable.",
                    optionalExt);
                continue;
            }

            if (optionalExt == "VK_KHR_dynamic_rendering_local_read" &&
                !vulkan13PromotedToCore &&
                !availableExtensionSet.Contains("VK_KHR_dynamic_rendering"))
            {
                Debug.VulkanWarning(
                    "[Vulkan] Optional extension {0} skipped because dynamic rendering is unavailable.",
                    optionalExt);
                continue;
            }

            if (vulkan12PromotedToCore && optionalExt == "VK_KHR_draw_indirect_count" && !supportedVulkan12Features.DrawIndirectCount)
            {
                Debug.VulkanWarning(
                    "[Vulkan] Optional extension {0} skipped because Vulkan 1.2 drawIndirectCount feature is unavailable.",
                    optionalExt);
                continue;
            }

            if (vulkan12PromotedToCore && optionalExt == "VK_EXT_descriptor_indexing" && !supportedVulkan12Features.DescriptorIndexing)
            {
                Debug.VulkanWarning(
                    "[Vulkan] Optional extension {0} skipped because Vulkan 1.2 descriptorIndexing feature is unavailable.",
                    optionalExt);
                continue;
            }

            if (vulkan12PromotedToCore && optionalExt == "VK_KHR_buffer_device_address" && !supportedVulkan12Features.BufferDeviceAddress)
            {
                Debug.VulkanWarning(
                    "[Vulkan] Optional extension {0} skipped because Vulkan 1.2 bufferDeviceAddress feature is unavailable.",
                    optionalExt);
                continue;
            }

            if (vulkan12PromotedToCore && optionalExt == "VK_KHR_timeline_semaphore" && !supportedVulkan12Features.TimelineSemaphore)
            {
                Debug.VulkanWarning(
                    "[Vulkan] Optional extension {0} skipped because Vulkan 1.2 timelineSemaphore feature is unavailable.",
                    optionalExt);
                continue;
            }

            if (availableExtensionSet.Contains(optionalExt))
            {
                extensionsToEnable.Add(optionalExt);
                Debug.Vulkan($"[Vulkan] Enabling optional extension: {optionalExt}");
            }
            else
            {
                Debug.Vulkan($"[Vulkan] Optional extension not supported: {optionalExt}");
            }
        }

        bool legacyBufferDeviceAddressRequested =
            extensionsToEnable.Contains("VK_EXT_buffer_device_address", StringComparer.Ordinal);
        var extensionsArray = VulkanDeviceContext.NormalizeDeviceExtensionSelection(
            extensionsToEnable,
            vulkan12PromotedToCore);
        if (legacyBufferDeviceAddressRequested &&
            !extensionsArray.Contains("VK_EXT_buffer_device_address", StringComparer.Ordinal))
        {
            Debug.Vulkan(
                "[Vulkan] Suppressed VK_EXT_buffer_device_address in favor of the KHR/core buffer-device-address path.");
        }
        VulkanDeviceExtensionSet enabledDeviceExtensions =
            _deviceContext.ValidateEnabledDeviceExtensions(extensionsArray);

        OutputRuntime.ValidateObsHookDeviceCompatibility(
            _deviceContext,
            availableExtensionSet,
            extensionsArray);

        bool drawIndirectCountExtensionEnabled = extensionsArray.Contains("VK_KHR_draw_indirect_count");
        bool descriptorIndexingExtensionEnabled = extensionsArray.Contains("VK_EXT_descriptor_indexing");
        bool descriptorIndexingRequestedByProfile = request.FeaturePolicy.EnableDescriptorIndexing;
        bool descriptorIndexingRequiredByStreamline =
            _outputRuntime._streamlineRequiredFeatures12.Contains("descriptorIndexing", StringComparer.Ordinal);
        _deviceContext.QueryDescriptorIndexingCapabilities();
        bool synchronization2ExtensionEnabled = extensionsArray.Contains("VK_KHR_synchronization2");
        _deviceContext.QuerySynchronization2Capabilities();

        bool descriptorIndexingCapabilityReady =
            _deviceContext.MutableCapabilities._supportsRuntimeDescriptorArray &&
            _deviceContext.MutableCapabilities._supportsDescriptorBindingPartiallyBound &&
            _deviceContext.MutableCapabilities._supportsDescriptorBindingVariableDescriptorCount &&
            _deviceContext.MutableCapabilities._supportsShaderSampledImageArrayNonUniformIndexing &&
            _deviceContext.MutableCapabilities._supportsDescriptorBindingUpdateAfterBind;

        bool enableDescriptorIndexing = descriptorIndexingExtensionEnabled &&
            (descriptorIndexingRequestedByProfile || descriptorIndexingRequiredByStreamline) &&
            descriptorIndexingCapabilityReady;

        bool nvMemoryDecompressionExtensionEnabled = extensionsArray.Contains("VK_NV_memory_decompression");
        bool nvMemoryDecompressionRequestedByProfile = request.FeaturePolicy.EnableRtxIoVulkanDecompression;

        _deviceContext.QueryNvMemoryDecompressionCapabilities(
            nvMemoryDecompressionExtensionEnabled,
            out bool nvMemoryDecompressionFeatureSupported,
            out MemoryDecompressionMethodFlagsNV nvMemoryDecompressionMethods,
            out ulong nvMaxDecompressionIndirectCount);

        bool enableNvMemoryDecompression =
            nvMemoryDecompressionExtensionEnabled &&
            nvMemoryDecompressionRequestedByProfile &&
            nvMemoryDecompressionFeatureSupported;

        bool nvCopyMemoryIndirectExtensionEnabled = extensionsArray.Contains("VK_NV_copy_memory_indirect");
        bool nvCopyMemoryIndirectRequestedByProfile = request.FeaturePolicy.EnableRtxIoVulkanCopyMemoryIndirect;

        _deviceContext.QueryNvCopyMemoryIndirectCapabilities(
            nvCopyMemoryIndirectExtensionEnabled,
            out bool nvCopyMemoryIndirectFeatureSupported,
            out ulong nvCopyMemoryIndirectSupportedQueues);

        bool enableNvCopyMemoryIndirect =
            nvCopyMemoryIndirectExtensionEnabled &&
            nvCopyMemoryIndirectRequestedByProfile &&
            nvCopyMemoryIndirectFeatureSupported;

        bool bufferDeviceAddressExtensionEnabled = extensionsArray.Contains("VK_KHR_buffer_device_address");
        _deviceContext.QueryBufferDeviceAddressCapabilities(out bool bufferDeviceAddressFeatureSupported);
        bool bufferDeviceAddressRequestedBySceneDatabase =
            request.FeaturePolicy.ActiveGeometryFetchMode == EVulkanGeometryFetchMode.BufferDeviceAddressPrototype ||
            request.FeaturePolicy.EnableBindlessMaterialTable;
        bool bufferDeviceAddressRequiredByStreamline =
            _outputRuntime._streamlineRequiredFeatures12.Contains("bufferDeviceAddress", StringComparer.Ordinal);
        bool enableBufferDeviceAddress =
            bufferDeviceAddressFeatureSupported &&
            (enableNvCopyMemoryIndirect ||
             bufferDeviceAddressRequestedBySceneDatabase ||
             bufferDeviceAddressExtensionEnabled ||
             bufferDeviceAddressRequiredByStreamline);

        bool dynamicRenderingExtensionEnabled = extensionsArray.Contains("VK_KHR_dynamic_rendering");
        _deviceContext.QueryDynamicRenderingCapabilities(
            dynamicRenderingExtensionEnabled,
            out bool dynamicRenderingFeatureSupported,
            out bool dynamicRenderingPromotedToCore);
        bool enableDynamicRenderingFeature = dynamicRenderingFeatureSupported;

        bool dynamicRenderingLocalReadExtensionEnabled = extensionsArray.Contains("VK_KHR_dynamic_rendering_local_read");
        _deviceContext.QueryDynamicRenderingLocalReadCapabilities(
            dynamicRenderingLocalReadExtensionEnabled,
            out bool dynamicRenderingLocalReadFeatureSupported,
            out bool dynamicRenderingLocalReadPromotedToCore,
            out bool dynamicRenderingLocalReadDepthStencilSupported,
            out bool dynamicRenderingLocalReadMultisampledSupported);
        bool enableDynamicRenderingLocalReadFeature =
            enableDynamicRenderingFeature &&
            dynamicRenderingLocalReadFeatureSupported;

        bool swapchainMaintenance1ExtensionEnabled =
            extensionsArray.Contains(SwapchainMaintenance1ExtensionName);
        bool swapchainMaintenance1FeatureSupported =
            swapchainMaintenance1ExtensionEnabled &&
            QuerySwapchainMaintenance1FeatureSupport();
        bool enableSwapchainMaintenance1Feature =
            swapchainMaintenance1ExtensionEnabled &&
            swapchainMaintenance1FeatureSupported;
        OutputRuntime.Desktop.Maintenance1Enabled = enableSwapchainMaintenance1Feature;

        bool shaderDrawParametersExtensionEnabled = extensionsArray.Contains("VK_KHR_shader_draw_parameters");
        _deviceContext.QueryShaderDrawParametersCapabilities(out bool shaderDrawParametersFeatureSupported);
        bool enableShaderDrawParametersFeature = shaderDrawParametersFeatureSupported;

        bool shaderOutputViewportIndexFeatureSupported =
            vulkan12PromotedToCore && supportedVulkan12Features.ShaderOutputViewportIndex;
        bool shaderOutputLayerFeatureSupported =
            vulkan12PromotedToCore && supportedVulkan12Features.ShaderOutputLayer;
        bool shaderViewportLayerPromotedToCore = vulkan12PromotedToCore;
        bool enableShaderOutputViewportIndexFeature = shaderOutputViewportIndexFeatureSupported;
        bool enableShaderOutputLayerFeature = shaderOutputLayerFeatureSupported;
        bool enableDrawIndirectCountFeature =
            vulkan12PromotedToCore &&
            supportedVulkan12Features.DrawIndirectCount;

        // Host query reset (core 1.2) remains available for externally synchronized maintenance.
        // Normal render-query reuse records queue-ordered command resets before rendering so
        // cached and freshly recorded command buffers share the same submission-safe contract.
        bool hostQueryResetFeatureSupported =
            vulkan12PromotedToCore && supportedVulkan12Features.HostQueryReset;
        bool enableHostQueryResetFeature = hostQueryResetFeatureSupported;

        bool multiviewExtensionEnabled = extensionsArray.Contains("VK_KHR_multiview");
        _deviceContext.QueryMultiviewCapabilities(
            multiviewExtensionEnabled,
            out bool multiviewFeatureSupported,
            out bool multiviewPromotedToCore);
        bool enableMultiviewFeature = multiviewFeatureSupported;

        bool indexTypeUint8ExtensionEnabled =
            extensionsArray.Contains("VK_EXT_index_type_uint8") ||
            extensionsArray.Contains("VK_KHR_index_type_uint8");
        _deviceContext.QueryIndexTypeUint8Capabilities(out bool indexTypeUint8FeatureSupported);
        bool enableIndexTypeUint8Feature = indexTypeUint8FeatureSupported;

        bool maintenance4ExtensionEnabled = extensionsArray.Contains("VK_KHR_maintenance4");
        _deviceContext.QueryMaintenance4Capabilities(
            maintenance4ExtensionEnabled,
            out bool maintenance4FeatureSupported);
        bool enableMaintenance4Feature = maintenance4FeatureSupported;

        bool maintenance5ExtensionEnabled = extensionsArray.Contains("VK_KHR_maintenance5");
        _deviceContext.QueryMaintenance5Capabilities(
            maintenance5ExtensionEnabled,
            out bool maintenance5FeatureSupported,
            out bool maintenance5PromotedToCore);
        bool enableMaintenance5Feature = maintenance5FeatureSupported;

        bool extendedFlagsExtensionAvailable = availableExtensionSet.Contains("VK_KHR_extended_flags");
        bool extendedFlagsExtensionEnabled = extensionsArray.Contains("VK_KHR_extended_flags");
        bool descriptorHeapExtensionAvailable = availableExtensionSet.Contains(VulkanDescriptorHeapExt.ExtensionName);
        bool descriptorHeapExtensionEnabled = extensionsArray.Contains(VulkanDescriptorHeapExt.ExtensionName);
        bool shaderUntypedPointersExtensionAvailable = availableExtensionSet.Contains(VulkanDescriptorHeapExt.ShaderUntypedPointersExtensionName);
        bool descriptorBufferExtensionAvailable = availableExtensionSet.Contains("VK_EXT_descriptor_buffer");
        bool memoryBudgetExtensionAvailable = availableExtensionSet.Contains("VK_EXT_memory_budget");
        bool memoryBudgetExtensionEnabled = extensionsArray.Contains("VK_EXT_memory_budget");
        bool memoryPriorityExtensionAvailable = availableExtensionSet.Contains("VK_EXT_memory_priority");
        _deviceContext.QueryMemoryPriorityCapabilities(
            memoryPriorityExtensionAvailable,
            out bool memoryPriorityFeatureSupported);

        bool shaderObjectExtensionAvailable = availableExtensionSet.Contains("VK_EXT_shader_object");
        _deviceContext.QueryShaderObjectCapabilities(
            shaderObjectExtensionAvailable,
            out bool shaderObjectFeatureSupported,
            out PhysicalDeviceShaderObjectPropertiesEXT shaderObjectProperties);

        bool accelerationStructureExtensionAvailable = availableExtensionSet.Contains("VK_KHR_acceleration_structure");
        _deviceContext.QueryAccelerationStructureCapabilities(
            accelerationStructureExtensionAvailable,
            out bool accelerationStructureFeatureSupported);
        bool rayTracingPipelineExtensionAvailable =
            availableExtensionSet.Contains("VK_KHR_ray_tracing_pipeline") &&
            availableExtensionSet.Contains("VK_KHR_deferred_host_operations");
        _deviceContext.QueryRayTracingPipelineCapabilities(
            rayTracingPipelineExtensionAvailable,
            out bool rayTracingPipelineFeatureSupported);
        bool rayQueryExtensionAvailable = availableExtensionSet.Contains("VK_KHR_ray_query");
        _deviceContext.QueryRayQueryCapabilities(
            rayQueryExtensionAvailable,
            out bool rayQueryFeatureSupported);
        bool deviceGeneratedCommandsExtensionAvailable = availableExtensionSet.Contains("VK_EXT_device_generated_commands");
        _deviceContext.QueryDeviceGeneratedCommandsCapabilities(
            deviceGeneratedCommandsExtensionAvailable,
            out bool deviceGeneratedCommandsFeatureSupported);
        bool khrDeviceFaultExtensionAvailable = availableExtensionSet.Contains(KhrDeviceFaultExtensionName);
        bool extDeviceFaultExtensionAvailable = availableExtensionSet.Contains(ExtDeviceFaultExtensionName);
        bool khrDeviceFaultExtensionEnabled = extensionsArray.Contains(KhrDeviceFaultExtensionName);
        bool extDeviceFaultExtensionEnabled = extensionsArray.Contains(ExtDeviceFaultExtensionName);
        VulkanKhrDeviceFaultCapabilityQuery khrDeviceFaultCapabilities =
            _deviceContext.QueryKhrDeviceFaultCapabilities(
                Api!,
                khrDeviceFaultExtensionEnabled);
        bool khrDeviceFaultFeatureSupported = khrDeviceFaultCapabilities.DeviceFault;
        bool khrDeviceFaultVendorBinaryFeatureSupported = khrDeviceFaultCapabilities.VendorBinary;
        bool khrDeviceFaultReportMaskedFeatureSupported = khrDeviceFaultCapabilities.ReportMasked;
        bool khrDeviceFaultDeviceLostOnMaskedFeatureSupported = khrDeviceFaultCapabilities.DeviceLostOnMasked;
        uint khrDeviceFaultMaxReportCount = khrDeviceFaultCapabilities.MaxReportCount;
        _deviceContext.QueryDeviceFaultCapabilities(
            extDeviceFaultExtensionEnabled,
            out bool extDeviceFaultFeatureSupported,
            out bool extDeviceFaultVendorBinaryFeatureSupported);
        bool enableKhrDeviceFaultFeature =
            _frameTelemetry._diagnosticOptions.RequestDeviceFault &&
            khrDeviceFaultExtensionEnabled &&
            khrDeviceFaultFeatureSupported;
        bool enableKhrDeviceFaultVendorBinary =
            enableKhrDeviceFaultFeature &&
            khrDeviceFaultVendorBinaryFeatureSupported;
        bool enableKhrDeviceFaultReportMasked =
            enableKhrDeviceFaultFeature &&
            _frameTelemetry._diagnosticOptions.Preset == EVulkanDiagnosticPreset.CrashDiagnostics &&
            khrDeviceFaultReportMaskedFeatureSupported;
        bool enableKhrDeviceFaultDeviceLostOnMasked =
            enableKhrDeviceFaultFeature &&
            _frameTelemetry._diagnosticOptions.RequestDeviceFaultDeviceLostOnMasked &&
            khrDeviceFaultDeviceLostOnMaskedFeatureSupported;
        bool enableExtDeviceFaultFeature =
            _frameTelemetry._diagnosticOptions.RequestDeviceFault &&
            extDeviceFaultExtensionEnabled &&
            extDeviceFaultFeatureSupported;
        bool enableDeviceFaultFeature =
            enableKhrDeviceFaultFeature ||
            enableExtDeviceFaultFeature;

        bool deviceAddressBindingReportExtensionAvailable = availableExtensionSet.Contains(ExtDeviceAddressBindingReportExtensionName);
        bool deviceAddressBindingReportExtensionEnabled = extensionsArray.Contains(ExtDeviceAddressBindingReportExtensionName);
        _deviceContext.QueryDeviceAddressBindingReportCapabilities(
            deviceAddressBindingReportExtensionEnabled,
            out bool deviceAddressBindingReportFeatureSupported);
        bool enableDeviceAddressBindingReportFeature =
            _frameTelemetry._diagnosticOptions.RequestDeviceAddressBindingReport &&
            deviceAddressBindingReportExtensionEnabled &&
            deviceAddressBindingReportFeatureSupported;

        bool nvDiagnosticCheckpointsExtensionAvailable = availableExtensionSet.Contains(NvDeviceDiagnosticCheckpointsExtensionName);
        bool nvDiagnosticCheckpointsExtensionEnabled = extensionsArray.Contains(NvDeviceDiagnosticCheckpointsExtensionName);
        bool enableNvDiagnosticCheckpoints = _frameTelemetry._diagnosticOptions.RequestNvDiagnosticCheckpoints && nvDiagnosticCheckpointsExtensionEnabled;

        bool nvDiagnosticsConfigExtensionAvailable = availableExtensionSet.Contains(NvDeviceDiagnosticsConfigExtensionName);
        bool nvDiagnosticsConfigExtensionEnabled = extensionsArray.Contains(NvDeviceDiagnosticsConfigExtensionName);
        _deviceContext.QueryNvDiagnosticsConfigCapabilities(
            nvDiagnosticsConfigExtensionEnabled,
            out bool nvDiagnosticsConfigFeatureSupported);
        bool enableNvDiagnosticsConfigFeature =
            _frameTelemetry._diagnosticOptions.RequestNvDiagnosticsConfig &&
            nvDiagnosticsConfigExtensionEnabled &&
            nvDiagnosticsConfigFeatureSupported;
        bool descriptorHeapDependenciesReady =
            descriptorHeapExtensionAvailable &&
            (vulkan14PromotedToCore ||
             ((maintenance5FeatureSupported || extendedFlagsExtensionAvailable) &&
              (bufferDeviceAddressFeatureSupported || vulkan12PromotedToCore))) &&
            shaderUntypedPointersExtensionAvailable;
        ResourceRuntime.Descriptors.QueryDescriptorHeapCapabilities(
            descriptorHeapExtensionAvailable,
            shaderUntypedPointersExtensionAvailable,
            out bool descriptorHeapFeatureSupported,
            out bool descriptorHeapCaptureReplaySupported,
            out PhysicalDeviceDescriptorHeapPropertiesEXTNative descriptorHeapProperties);
        bool enableDescriptorHeapFeature =
            descriptorHeapExtensionEnabled &&
            descriptorHeapDependenciesReady &&
            descriptorHeapFeatureSupported;

        _deviceContext.QueryTimelineSemaphoreCapabilities(out bool timelineSemaphoreFeatureSupported);
        bool enableTimelineSemaphoreFeature = timelineSemaphoreFeatureSupported;
        bool enableSynchronization2Feature = synchronization2ExtensionEnabled && _deviceContext.MutableCapabilities._supportsSynchronization2Feature;

        bool depthClipControlExtensionEnabled = extensionsArray.Contains(VulkanDepthClipControlExt.ExtensionName);
        _deviceContext.QueryDepthClipControlCapabilities(
            depthClipControlExtensionEnabled,
            out bool depthClipControlFeatureSupported);
        bool enableDepthClipControlFeature = depthClipControlExtensionEnabled && depthClipControlFeatureSupported;

        bool meshShaderExtensionEnabled = extensionsArray.Contains(ExtMeshShader.ExtensionName, StringComparer.Ordinal);
        _deviceContext.QueryMeshShaderCapabilities(
            meshShaderExtensionEnabled,
            out bool taskShaderFeatureSupported,
            out bool meshShaderFeatureSupported,
            out bool meshShaderQueriesSupported,
            out PhysicalDeviceMeshShaderPropertiesEXT meshShaderProperties);
        bool enableMeshShaderFeature =
            meshShaderExtensionEnabled &&
            taskShaderFeatureSupported &&
            meshShaderFeatureSupported;
        bool enableMeshShaderQueries = enableMeshShaderFeature && meshShaderQueriesSupported;

        bool graphicsPipelineLibraryDependencyEnabled = extensionsArray.Contains("VK_KHR_pipeline_library");
        bool graphicsPipelineLibraryExtensionEnabled =
            graphicsPipelineLibraryDependencyEnabled &&
            extensionsArray.Contains("VK_EXT_graphics_pipeline_library");
        _deviceContext.QueryGraphicsPipelineLibraryCapabilities(
            graphicsPipelineLibraryExtensionEnabled,
            out bool graphicsPipelineLibraryFeatureSupported);
        bool enableGraphicsPipelineLibraryFeature =
            graphicsPipelineLibraryExtensionEnabled &&
            graphicsPipelineLibraryFeatureSupported;

        bool pipelineCreationCacheControlAvailable =
            vulkan13PromotedToCore ||
            extensionsArray.Contains("VK_EXT_pipeline_creation_cache_control");
        PhysicalDevicePipelineCreationCacheControlFeatures supportedPipelineCreationCacheControlFeatures = new()
        {
            SType = StructureType.PhysicalDevicePipelineCreationCacheControlFeatures,
        };
        if (pipelineCreationCacheControlAvailable)
        {
            PhysicalDeviceFeatures2 pipelineCreationCacheControlFeatures2 = new()
            {
                SType = StructureType.PhysicalDeviceFeatures2,
                PNext = &supportedPipelineCreationCacheControlFeatures,
            };
            Api.GetPhysicalDeviceFeatures2(_deviceContext.PhysicalDevice, &pipelineCreationCacheControlFeatures2);
        }
        bool enablePipelineCreationCacheControlFeature =
            pipelineCreationCacheControlAvailable &&
            supportedPipelineCreationCacheControlFeatures.PipelineCreationCacheControl;
        ResourceRuntime.PipelineManager._supportsPipelineCreationCacheControl = enablePipelineCreationCacheControlFeature;

        bool transformFeedbackExtensionEnabled = extensionsArray.Contains(ExtTransformFeedback.ExtensionName);
        _deviceContext.QueryTransformFeedbackCapabilities(
            transformFeedbackExtensionEnabled,
            out bool transformFeedbackFeatureSupported,
            out bool transformFeedbackGeometryStreamsSupported,
            out PhysicalDeviceTransformFeedbackPropertiesEXT transformFeedbackProperties);
        bool enableTransformFeedbackFeature =
            transformFeedbackExtensionEnabled &&
            transformFeedbackFeatureSupported;

        bool primitivesGeneratedExtensionEnabled = extensionsArray.Contains("VK_EXT_primitives_generated_query");
        PhysicalDevicePrimitivesGeneratedQueryFeaturesEXT primitivesGeneratedFeatures = new()
        {
            SType = StructureType.PhysicalDevicePrimitivesGeneratedQueryFeaturesExt,
        };
        if (primitivesGeneratedExtensionEnabled)
        {
            PhysicalDeviceFeatures2 primitivesGeneratedFeatures2 = new()
            {
                SType = StructureType.PhysicalDeviceFeatures2,
                PNext = &primitivesGeneratedFeatures,
            };
            Api.GetPhysicalDeviceFeatures2(_deviceContext.PhysicalDevice, &primitivesGeneratedFeatures2);
        }
        bool enablePrimitivesGeneratedQuery =
            primitivesGeneratedExtensionEnabled &&
            primitivesGeneratedFeatures.PrimitivesGeneratedQuery;

        bool fragmentShadingRateExtensionEnabled = extensionsArray.Contains("VK_KHR_fragment_shading_rate");
        _deviceContext.QueryFragmentShadingRateCapabilities(
            fragmentShadingRateExtensionEnabled,
            out bool fragmentShadingRateFeatureSupported,
            out bool pipelineFragmentShadingRateSupported,
            out bool primitiveFragmentShadingRateSupported,
            out bool attachmentFragmentShadingRateSupported,
            out PhysicalDeviceFragmentShadingRatePropertiesKHR fragmentShadingRateProperties);
        bool enableFragmentShadingRateFeature =
            fragmentShadingRateExtensionEnabled &&
            fragmentShadingRateFeatureSupported;

        bool fragmentDensityMapExtensionEnabled = extensionsArray.Contains("VK_EXT_fragment_density_map");
        _deviceContext.QueryFragmentDensityMapCapabilities(
            fragmentDensityMapExtensionEnabled,
            out bool fragmentDensityMapFeatureSupported,
            out bool fragmentDensityMapDynamicSupported,
            out bool fragmentDensityMapNonSubsampledImagesSupported);
        bool enableFragmentDensityMapFeature =
            fragmentDensityMapExtensionEnabled &&
            fragmentDensityMapFeatureSupported;

        _deviceContext.MutableCapabilities._nvMemoryDecompressionMethods = enableNvMemoryDecompression ? nvMemoryDecompressionMethods : 0;
        _deviceContext.MutableCapabilities._nvMaxMemoryDecompressionIndirectCount = enableNvMemoryDecompression ? nvMaxDecompressionIndirectCount : 0;
        _deviceContext.MutableCapabilities._nvCopyMemoryIndirectSupportedQueues = enableNvCopyMemoryIndirect ? nvCopyMemoryIndirectSupportedQueues : 0;

        PhysicalDevicePrivateDataFeatures privateDataFeatureEnable = new()
        {
            SType = StructureType.PhysicalDevicePrivateDataFeatures,
            PNext = null,
            PrivateData = enablePrivateDataFeature,
        };

        PhysicalDeviceDescriptorIndexingFeatures descriptorIndexingFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceDescriptorIndexingFeatures,
            PNext = null,
            RuntimeDescriptorArray = enableDescriptorIndexing,
            DescriptorBindingPartiallyBound = enableDescriptorIndexing,
            DescriptorBindingSampledImageUpdateAfterBind = enableDescriptorIndexing,
            DescriptorBindingStorageImageUpdateAfterBind = enableDescriptorIndexing && _deviceContext.MutableCapabilities._supportsDescriptorBindingStorageImageUpdateAfterBind,
            DescriptorBindingStorageBufferUpdateAfterBind = enableDescriptorIndexing,
            DescriptorBindingUniformBufferUpdateAfterBind = enableDescriptorIndexing,
            DescriptorBindingVariableDescriptorCount = enableDescriptorIndexing,
            ShaderSampledImageArrayNonUniformIndexing = enableDescriptorIndexing,
        };

        PhysicalDeviceMemoryDecompressionFeaturesNV memoryDecompressionFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceMemoryDecompressionFeaturesNV,
            PNext = null,
            MemoryDecompression = enableNvMemoryDecompression,
        };

        PhysicalDeviceCopyMemoryIndirectFeaturesNV copyMemoryIndirectFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceCopyMemoryIndirectFeaturesNV,
            PNext = null,
            IndirectCopy = enableNvCopyMemoryIndirect,
        };

        PhysicalDeviceBufferDeviceAddressFeatures bufferDeviceAddressFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceBufferDeviceAddressFeatures,
            PNext = null,
            BufferDeviceAddress = enableBufferDeviceAddress,
        };

        PhysicalDeviceDynamicRenderingFeatures dynamicRenderingFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceDynamicRenderingFeatures,
            PNext = null,
            DynamicRendering = enableDynamicRenderingFeature,
        };

        PhysicalDeviceDynamicRenderingLocalReadFeatures dynamicRenderingLocalReadFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceDynamicRenderingLocalReadFeatures,
            PNext = null,
            DynamicRenderingLocalRead = enableDynamicRenderingLocalReadFeature,
        };

        PhysicalDeviceDynamicRenderingLocalReadFeaturesKHR dynamicRenderingLocalReadFeatureEnableKhr = new()
        {
            SType = StructureType.PhysicalDeviceDynamicRenderingLocalReadFeaturesKhr,
            PNext = null,
            DynamicRenderingLocalRead = enableDynamicRenderingLocalReadFeature,
        };

        PhysicalDeviceSwapchainMaintenance1FeaturesEXT swapchainMaintenance1FeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceSwapchainMaintenance1FeaturesExt,
            PNext = null,
            SwapchainMaintenance1 = enableSwapchainMaintenance1Feature,
        };

        PhysicalDeviceVulkan11Features vulkan11FeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceVulkan11Features,
            PNext = null,
            ShaderDrawParameters = enableShaderDrawParametersFeature,
            Multiview = enableMultiviewFeature,
        };

        PhysicalDeviceVulkan12Features vulkan12FeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceVulkan12Features,
            PNext = null,
            DrawIndirectCount = enableDrawIndirectCountFeature,
            DescriptorIndexing = descriptorIndexingExtensionEnabled && supportedVulkan12Features.DescriptorIndexing,
            RuntimeDescriptorArray = enableDescriptorIndexing,
            DescriptorBindingPartiallyBound = enableDescriptorIndexing,
            DescriptorBindingSampledImageUpdateAfterBind = enableDescriptorIndexing,
            DescriptorBindingStorageImageUpdateAfterBind = enableDescriptorIndexing && _deviceContext.MutableCapabilities._supportsDescriptorBindingStorageImageUpdateAfterBind,
            DescriptorBindingStorageBufferUpdateAfterBind = enableDescriptorIndexing,
            DescriptorBindingUniformBufferUpdateAfterBind = enableDescriptorIndexing,
            DescriptorBindingVariableDescriptorCount = enableDescriptorIndexing,
            ShaderSampledImageArrayNonUniformIndexing = enableDescriptorIndexing,
            TimelineSemaphore = enableTimelineSemaphoreFeature,
            BufferDeviceAddress = enableBufferDeviceAddress,
            ShaderOutputViewportIndex = enableShaderOutputViewportIndexFeature,
            ShaderOutputLayer = enableShaderOutputLayerFeature,
            HostQueryReset = enableHostQueryResetFeature,
        };

        PhysicalDeviceVulkan13Features vulkan13FeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceVulkan13Features,
            PNext = null,
            DynamicRendering = enableDynamicRenderingFeature,
            Synchronization2 = enableSynchronization2Feature,
            Maintenance4 = enableMaintenance4Feature,
            PipelineCreationCacheControl = enablePipelineCreationCacheControlFeature,
            PrivateData = enablePrivateDataFeature,
        };

        PhysicalDeviceIndexTypeUint8FeaturesEXT indexTypeUint8FeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceIndexTypeUint8FeaturesExt,
            PNext = null,
            IndexTypeUint8 = enableIndexTypeUint8Feature,
        };

        PhysicalDeviceMaintenance4Features maintenance4FeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceMaintenance4Features,
            PNext = null,
            Maintenance4 = enableMaintenance4Feature,
        };

        PhysicalDeviceMaintenance5Features maintenance5FeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceMaintenance5Features,
            PNext = null,
            Maintenance5 = enableMaintenance5Feature,
        };

        PhysicalDeviceMaintenance5FeaturesKHR maintenance5FeatureEnableKhr = new()
        {
            SType = StructureType.PhysicalDeviceMaintenance5FeaturesKhr,
            PNext = null,
            Maintenance5 = enableMaintenance5Feature,
        };

        PhysicalDeviceTimelineSemaphoreFeatures timelineSemaphoreFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceTimelineSemaphoreFeatures,
            PNext = null,
            TimelineSemaphore = enableTimelineSemaphoreFeature,
        };

        PhysicalDeviceHostQueryResetFeatures hostQueryResetFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceHostQueryResetFeatures,
            PNext = null,
            HostQueryReset = enableHostQueryResetFeature,
        };

        PhysicalDeviceSynchronization2Features synchronization2FeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceSynchronization2Features,
            PNext = null,
            Synchronization2 = enableSynchronization2Feature,
        };

        PhysicalDeviceDepthClipControlFeaturesEXTNative depthClipControlFeatureEnable = new()
        {
            SType = VulkanDepthClipControlExt.PhysicalDeviceFeaturesSType,
            PNext = null,
            DepthClipControl = enableDepthClipControlFeature,
        };

        PhysicalDeviceMeshShaderFeaturesEXT meshShaderFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceMeshShaderFeaturesExt,
            PNext = null,
            TaskShader = enableMeshShaderFeature,
            MeshShader = enableMeshShaderFeature,
            MeshShaderQueries = enableMeshShaderQueries,
        };

        PhysicalDevicePrimitivesGeneratedQueryFeaturesEXT primitivesGeneratedFeatureEnable = new()
        {
            SType = StructureType.PhysicalDevicePrimitivesGeneratedQueryFeaturesExt,
            PNext = null,
            PrimitivesGeneratedQuery = enablePrimitivesGeneratedQuery,
            PrimitivesGeneratedQueryWithRasterizerDiscard = enablePrimitivesGeneratedQuery && primitivesGeneratedFeatures.PrimitivesGeneratedQueryWithRasterizerDiscard,
            PrimitivesGeneratedQueryWithNonZeroStreams = enablePrimitivesGeneratedQuery && primitivesGeneratedFeatures.PrimitivesGeneratedQueryWithNonZeroStreams,
        };

        PhysicalDeviceGraphicsPipelineLibraryFeaturesEXT graphicsPipelineLibraryFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceGraphicsPipelineLibraryFeaturesExt,
            PNext = null,
            GraphicsPipelineLibrary = enableGraphicsPipelineLibraryFeature,
        };

        PhysicalDevicePipelineCreationCacheControlFeatures pipelineCreationCacheControlFeatureEnable = new()
        {
            SType = StructureType.PhysicalDevicePipelineCreationCacheControlFeatures,
            PNext = null,
            PipelineCreationCacheControl = enablePipelineCreationCacheControlFeature,
        };

        PhysicalDeviceTransformFeedbackFeaturesEXT transformFeedbackFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceTransformFeedbackFeaturesExt,
            PNext = null,
            TransformFeedback = enableTransformFeedbackFeature,
            GeometryStreams = enableTransformFeedbackFeature && transformFeedbackGeometryStreamsSupported,
        };

        PhysicalDeviceFragmentShadingRateFeaturesKHR fragmentShadingRateFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceFragmentShadingRateFeaturesKhr,
            PNext = null,
            PipelineFragmentShadingRate = enableFragmentShadingRateFeature && pipelineFragmentShadingRateSupported,
            PrimitiveFragmentShadingRate = enableFragmentShadingRateFeature && primitiveFragmentShadingRateSupported,
            AttachmentFragmentShadingRate = enableFragmentShadingRateFeature && attachmentFragmentShadingRateSupported,
        };

        PhysicalDeviceFragmentDensityMapFeaturesEXT fragmentDensityMapFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceFragmentDensityMapFeaturesExt,
            PNext = null,
            FragmentDensityMap = enableFragmentDensityMapFeature,
            FragmentDensityMapDynamic = enableFragmentDensityMapFeature && fragmentDensityMapDynamicSupported,
            FragmentDensityMapNonSubsampledImages = enableFragmentDensityMapFeature && fragmentDensityMapNonSubsampledImagesSupported,
        };

        PhysicalDeviceFaultFeaturesEXT deviceFaultFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceFaultFeaturesExt,
            PNext = null,
            DeviceFault = enableExtDeviceFaultFeature,
            DeviceFaultVendorBinary = enableExtDeviceFaultFeature && extDeviceFaultVendorBinaryFeatureSupported,
        };

        VulkanKhrPhysicalDeviceFaultFeatures khrDeviceFaultFeatureEnable = new()
        {
            SType = (StructureType)1000573000,
            PNext = null,
            DeviceFault = enableKhrDeviceFaultFeature ? 1u : 0u,
            DeviceFaultVendorBinary = enableKhrDeviceFaultVendorBinary ? 1u : 0u,
            DeviceFaultReportMasked = enableKhrDeviceFaultReportMasked ? 1u : 0u,
            DeviceFaultDeviceLostOnMasked = enableKhrDeviceFaultDeviceLostOnMasked ? 1u : 0u,
        };

        PhysicalDeviceAddressBindingReportFeaturesEXT deviceAddressBindingReportFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceAddressBindingReportFeaturesExt,
            PNext = null,
            ReportAddressBinding = enableDeviceAddressBindingReportFeature,
        };

        PhysicalDeviceDiagnosticsConfigFeaturesNV nvDiagnosticsConfigFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceDiagnosticsConfigFeaturesNV,
            PNext = null,
            DiagnosticsConfig = enableNvDiagnosticsConfigFeature,
        };

        DeviceDiagnosticsConfigCreateInfoNV nvDiagnosticsConfigCreateInfo = new()
        {
            SType = StructureType.DeviceDiagnosticsConfigCreateInfoNV,
            PNext = null,
            Flags = DeviceDiagnosticsConfigFlagsNV.ResourceTrackingBitNV |
                    DeviceDiagnosticsConfigFlagsNV.AutomaticCheckpointsBitNV |
                    DeviceDiagnosticsConfigFlagsNV.ShaderErrorReportingBitNV,
        };

        PhysicalDeviceDescriptorHeapFeaturesEXTNative descriptorHeapFeatureEnable = new()
        {
            SType = VulkanDescriptorHeapExt.PhysicalDeviceDescriptorHeapFeaturesSType,
            PNext = null,
            DescriptorHeap = enableDescriptorHeapFeature,
            DescriptorHeapCaptureReplay = false,
        };

        // Keep promoted feature structs separate. Mixing VkPhysicalDeviceVulkan12/13Features
        // with their promoted per-feature structs is invalid. A normal Vulkan device path can
        // consolidate Streamline requirements into the aggregate structs. OpenXR runtimes may
        // prepend their own promoted feature structs, so that path must remain granular.
        bool hasStreamlineVulkan12Features = _outputRuntime._streamlineRequiredFeatures12.Length > 0;
        bool hasStreamlineVulkan13Features = _outputRuntime._streamlineRequiredFeatures13.Length > 0;
        bool useGranularOpenXrStreamlineFeatureChain =
            _deviceContext.OpenXrBootstrapContext is not null &&
            (hasStreamlineVulkan12Features || hasStreamlineVulkan13Features);
        if (useGranularOpenXrStreamlineFeatureChain &&
            !TryUseGranularOpenXrStreamlineFeatureChain(
                _outputRuntime._streamlineRequiredFeatures12,
                _outputRuntime._streamlineRequiredFeatures13,
                out string granularFeatureFailure))
        {
            throw new InvalidOperationException(granularFeatureFailure);
        }

        bool useVulkan12FeatureEnable =
            hasStreamlineVulkan12Features && !useGranularOpenXrStreamlineFeatureChain;
        bool useVulkan13FeatureEnable =
            hasStreamlineVulkan13Features && !useGranularOpenXrStreamlineFeatureChain;
        if (useVulkan12FeatureEnable || useVulkan13FeatureEnable)
        {
            PhysicalDeviceVulkan12Features streamlineSupportedFeatures12 = new()
            {
                SType = StructureType.PhysicalDeviceVulkan12Features,
            };
            PhysicalDeviceVulkan13Features streamlineSupportedFeatures13 = new()
            {
                SType = StructureType.PhysicalDeviceVulkan13Features,
            };
            PhysicalDeviceFeatures2 streamlineSupportedFeatures = new()
            {
                SType = StructureType.PhysicalDeviceFeatures2,
            };

            if (useVulkan12FeatureEnable)
            {
                streamlineSupportedFeatures.PNext = &streamlineSupportedFeatures12;
                streamlineSupportedFeatures12.PNext = useVulkan13FeatureEnable ? &streamlineSupportedFeatures13 : null;
            }
            else
            {
                streamlineSupportedFeatures.PNext = &streamlineSupportedFeatures13;
            }

            Api!.GetPhysicalDeviceFeatures2(_deviceContext.PhysicalDevice, &streamlineSupportedFeatures);
            if (useVulkan12FeatureEnable)
            {
                PopulateStreamlineRequiredFeatures(
                    ref vulkan12FeatureEnable,
                    in streamlineSupportedFeatures12,
                    _outputRuntime._streamlineRequiredFeatures12,
                    "Vulkan 1.2");
            }

            if (useVulkan13FeatureEnable)
            {
                PopulateStreamlineRequiredFeatures(
                    ref vulkan13FeatureEnable,
                    in streamlineSupportedFeatures13,
                    _outputRuntime._streamlineRequiredFeatures13,
                    "Vulkan 1.3");
            }
        }

        bool enableStreamlineOpticalFlow = _outputRuntime._streamlineQueueRequirements.OpticalFlowQueues > 0;
        PhysicalDeviceOpticalFlowFeaturesNV opticalFlowFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceOpticalFlowFeaturesNV,
            PNext = null,
            OpticalFlow = enableStreamlineOpticalFlow,
        };

        if (enableStreamlineOpticalFlow)
        {
            PhysicalDeviceOpticalFlowFeaturesNV supportedOpticalFlow = new()
            {
                SType = StructureType.PhysicalDeviceOpticalFlowFeaturesNV,
            };
            PhysicalDeviceFeatures2 supportedOpticalFlowFeatures = new()
            {
                SType = StructureType.PhysicalDeviceFeatures2,
                PNext = &supportedOpticalFlow,
            };
            Api!.GetPhysicalDeviceFeatures2(_deviceContext.PhysicalDevice, &supportedOpticalFlowFeatures);
            if (!supportedOpticalFlow.OpticalFlow)
                throw new NotSupportedException("Streamline DLSS-G requires VkPhysicalDeviceOpticalFlowFeaturesNV::opticalFlow, but the selected Vulkan device does not support it.");
        }

        VulkanDeviceFeatureChainBuilder featureChainBuilder = new();
        featureChainBuilder.Prepend(
            ref privateDataFeatureEnable,
            enablePrivateDataFeature && !useVulkan13FeatureEnable);
        featureChainBuilder.Prepend(
            ref descriptorIndexingFeatureEnable,
            enableDescriptorIndexing && !useVulkan12FeatureEnable);
        featureChainBuilder.Prepend(ref memoryDecompressionFeatureEnable, enableNvMemoryDecompression);
        featureChainBuilder.Prepend(ref copyMemoryIndirectFeatureEnable, enableNvCopyMemoryIndirect);
        featureChainBuilder.Prepend(
            ref bufferDeviceAddressFeatureEnable,
            enableBufferDeviceAddress && !useVulkan12FeatureEnable);
        featureChainBuilder.Prepend(ref descriptorHeapFeatureEnable, enableDescriptorHeapFeature);
        featureChainBuilder.Prepend(
            ref dynamicRenderingFeatureEnable,
            enableDynamicRenderingFeature && !useVulkan13FeatureEnable);
        if (dynamicRenderingLocalReadPromotedToCore)
            featureChainBuilder.Prepend(ref dynamicRenderingLocalReadFeatureEnable, enableDynamicRenderingLocalReadFeature);
        else
            featureChainBuilder.Prepend(ref dynamicRenderingLocalReadFeatureEnableKhr, enableDynamicRenderingLocalReadFeature);
        featureChainBuilder.Prepend(ref swapchainMaintenance1FeatureEnable, enableSwapchainMaintenance1Feature);
        featureChainBuilder.Prepend(
            ref vulkan11FeatureEnable,
            enableShaderDrawParametersFeature || enableMultiviewFeature);
        featureChainBuilder.Prepend(ref vulkan12FeatureEnable, useVulkan12FeatureEnable);
        featureChainBuilder.Prepend(ref vulkan13FeatureEnable, useVulkan13FeatureEnable);
        featureChainBuilder.Prepend(ref indexTypeUint8FeatureEnable, enableIndexTypeUint8Feature);
        featureChainBuilder.Prepend(
            ref maintenance4FeatureEnable,
            enableMaintenance4Feature && !useVulkan13FeatureEnable);
        if (maintenance5PromotedToCore)
            featureChainBuilder.Prepend(ref maintenance5FeatureEnable, enableMaintenance5Feature);
        else
            featureChainBuilder.Prepend(ref maintenance5FeatureEnableKhr, enableMaintenance5Feature);
        featureChainBuilder.Prepend(
            ref timelineSemaphoreFeatureEnable,
            enableTimelineSemaphoreFeature && !useVulkan12FeatureEnable);
        featureChainBuilder.Prepend(
            ref hostQueryResetFeatureEnable,
            enableHostQueryResetFeature && !useVulkan12FeatureEnable);
        featureChainBuilder.Prepend(
            ref synchronization2FeatureEnable,
            enableSynchronization2Feature && !useVulkan13FeatureEnable);
        featureChainBuilder.Prepend(ref depthClipControlFeatureEnable, enableDepthClipControlFeature);
        featureChainBuilder.Prepend(ref meshShaderFeatureEnable, enableMeshShaderFeature);
        featureChainBuilder.Prepend(ref primitivesGeneratedFeatureEnable, enablePrimitivesGeneratedQuery);
        featureChainBuilder.Prepend(
            ref graphicsPipelineLibraryFeatureEnable,
            enableGraphicsPipelineLibraryFeature);
        featureChainBuilder.Prepend(
            ref pipelineCreationCacheControlFeatureEnable,
            enablePipelineCreationCacheControlFeature && !useVulkan13FeatureEnable);
        featureChainBuilder.Prepend(ref transformFeedbackFeatureEnable, enableTransformFeedbackFeature);
        featureChainBuilder.Prepend(ref fragmentShadingRateFeatureEnable, enableFragmentShadingRateFeature);
        featureChainBuilder.Prepend(ref fragmentDensityMapFeatureEnable, enableFragmentDensityMapFeature);
        featureChainBuilder.Prepend(ref deviceFaultFeatureEnable, enableExtDeviceFaultFeature);
        featureChainBuilder.Prepend(ref khrDeviceFaultFeatureEnable, enableKhrDeviceFaultFeature);
        featureChainBuilder.Prepend(
            ref deviceAddressBindingReportFeatureEnable,
            enableDeviceAddressBindingReportFeature);
        featureChainBuilder.Prepend(ref nvDiagnosticsConfigFeatureEnable, enableNvDiagnosticsConfigFeature);
        featureChainBuilder.Prepend(ref opticalFlowFeatureEnable, enableStreamlineOpticalFlow);

        PhysicalDeviceFeatures2 featureChain = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = featureChainBuilder.Head,
            Features = deviceFeatures,
        };

        void* deviceCreatePNext = &featureChain;
        if (enableNvDiagnosticsConfigFeature)
        {
            nvDiagnosticsConfigCreateInfo.PNext = deviceCreatePNext;
            deviceCreatePNext = &nvDiagnosticsConfigCreateInfo;
        }

        _deviceContext.CreateNativeLogicalDevice(
            Api!,
            new NativeLogicalDeviceCreateRequest(
                queueCreateInfos,
                (uint)uniqueQueueFamilies.Length,
                deviceCreatePNext,
                extensionsArray,
                enabledDeviceExtensions,
                supportsMultipleGraphicsQueues));

        bool descriptorHeapNativeApiAvailable = false;
        string descriptorHeapNativeApiReason = string.Empty;
        if (enableDescriptorHeapFeature)
            descriptorHeapNativeApiAvailable = ResourceRuntime.Descriptors.TryInitializeDescriptorHeapNativeApi(out descriptorHeapNativeApiReason);

        _deviceContext.MutableCapabilities._supportsDescriptorIndexing = enableDescriptorIndexing;
        _deviceContext.MutableCapabilities._supportsNvMemoryDecompression = enableNvMemoryDecompression;
        _deviceContext.MutableCapabilities._supportsNvCopyMemoryIndirect = enableNvCopyMemoryIndirect;
        _deviceContext.MutableCapabilities._supportsBufferDeviceAddress = enableBufferDeviceAddress;
        _deviceContext.MutableCapabilities._supportsDynamicRendering = dynamicRenderingFeatureSupported;
        _deviceContext.MutableCapabilities._supportsVulkan14 = vulkan14PromotedToCore;
        _deviceContext.MutableCapabilities._supportsDynamicRenderingLocalRead = enableDynamicRenderingLocalReadFeature;
        _deviceContext.MutableCapabilities._supportsDynamicRenderingLocalReadStorageResources = enableDynamicRenderingLocalReadFeature;
        _deviceContext.MutableCapabilities._supportsDynamicRenderingLocalReadColorAttachments = enableDynamicRenderingLocalReadFeature;
        _deviceContext.MutableCapabilities._supportsDynamicRenderingLocalReadDepthStencilAttachments =
            enableDynamicRenderingLocalReadFeature && dynamicRenderingLocalReadDepthStencilSupported;
        _deviceContext.MutableCapabilities._supportsDynamicRenderingLocalReadMultisampledAttachments =
            enableDynamicRenderingLocalReadFeature && dynamicRenderingLocalReadMultisampledSupported;
        _deviceContext.MutableCapabilities._supportsMaintenance4 = enableMaintenance4Feature;
        _deviceContext.MutableCapabilities._supportsMaintenance5 = enableMaintenance5Feature;
        _deviceContext.MutableCapabilities._supportsExtendedFlags = extendedFlagsExtensionEnabled;
        ResourceRuntime.Descriptors._descriptorHeapFeatureSupported = descriptorHeapFeatureSupported;
        ResourceRuntime.Descriptors._descriptorHeapCaptureReplaySupported = descriptorHeapCaptureReplaySupported;
        ResourceRuntime.Descriptors._descriptorHeapProperties = descriptorHeapFeatureSupported ? descriptorHeapProperties : default;
        _deviceContext.MutableCapabilities._supportsDescriptorHeap = enableDescriptorHeapFeature && descriptorHeapNativeApiAvailable;
        _deviceContext.MutableCapabilities._supportsShaderObject = shaderObjectFeatureSupported;
        _deviceContext.MutableCapabilities._supportsMemoryBudget = memoryBudgetExtensionAvailable && memoryBudgetExtensionEnabled;
        _deviceContext.MutableCapabilities._supportsMemoryPriority = memoryPriorityFeatureSupported;
        _deviceContext.MutableCapabilities._supportsAccelerationStructure = accelerationStructureFeatureSupported;
        _deviceContext.MutableCapabilities._supportsRayTracingPipeline = rayTracingPipelineFeatureSupported;
        _deviceContext.MutableCapabilities._supportsRayQuery = rayQueryFeatureSupported;
        _deviceContext.MutableCapabilities._supportsDeviceGeneratedCommands = deviceGeneratedCommandsFeatureSupported;
        _deviceContext.DeviceFaultFacility.PublishKhrSupport(
            enableKhrDeviceFaultFeature,
            enableKhrDeviceFaultVendorBinary,
            enableKhrDeviceFaultReportMasked,
            enableKhrDeviceFaultDeviceLostOnMasked,
            khrDeviceFaultMaxReportCount);
        _deviceContext.DeviceFaultFacility.PublishExtSupport(
            enableExtDeviceFaultFeature,
            enableExtDeviceFaultFeature && extDeviceFaultVendorBinaryFeatureSupported);
        _deviceContext.MutableCapabilities._supportsDeviceAddressBindingReport = enableDeviceAddressBindingReportFeature;
        _deviceContext.MutableCapabilities._supportsNvDiagnosticCheckpoints = enableNvDiagnosticCheckpoints;
        _deviceContext.MutableCapabilities._supportsNvDiagnosticsConfig = enableNvDiagnosticsConfigFeature;
        _deviceContext.MutableCapabilities._shaderObjectProperties = shaderObjectFeatureSupported ? shaderObjectProperties : default;
        _deviceContext.MutableCapabilities._supportsIndexTypeUint8 = enableIndexTypeUint8Feature;
        _deviceContext.MutableCapabilities._supportsTimelineSemaphores = enableTimelineSemaphoreFeature;
        _deviceContext.MutableCapabilities._supportsSynchronization2 = enableSynchronization2Feature;
        _deviceContext.MutableCapabilities._supportsDepthClipControl = enableDepthClipControlFeature;
        _deviceContext.MutableCapabilities._supportsGraphicsPipelineLibrary = enableGraphicsPipelineLibraryFeature;
        _deviceContext.MutableCapabilities._supportsTransformFeedback = enableTransformFeedbackFeature;
        _deviceContext.MutableCapabilities._supportsTransformFeedbackGeometryStreams = enableTransformFeedbackFeature && transformFeedbackGeometryStreamsSupported;
        _deviceContext.MutableCapabilities._supportsTransformFeedbackQueries = enableTransformFeedbackFeature && transformFeedbackProperties.TransformFeedbackQueries;
        _deviceContext.MutableCapabilities._supportsTransformFeedbackDraw = enableTransformFeedbackFeature && transformFeedbackProperties.TransformFeedbackDraw;
        _deviceContext.MutableCapabilities._transformFeedbackProperties = enableTransformFeedbackFeature ? transformFeedbackProperties : default;
        _deviceContext.MutableCapabilities._supportsHostQueryReset = enableHostQueryResetFeature;
        _deviceContext.MutableCapabilities._supportsVulkanFragmentShadingRate = enableFragmentShadingRateFeature;
        _deviceContext.MutableCapabilities._supportsVulkanFragmentShadingRateAttachment = enableFragmentShadingRateFeature && attachmentFragmentShadingRateSupported;
        _deviceContext.MutableCapabilities._fragmentShadingRateProperties = enableFragmentShadingRateFeature ? fragmentShadingRateProperties : default;
        _deviceContext.MutableCapabilities._supportsVulkanFragmentDensityMap = enableFragmentDensityMapFeature;
        _deviceContext.MutableCapabilities._supportsVulkanFragmentDensityMapDynamic = enableFragmentDensityMapFeature && fragmentDensityMapDynamicSupported;
        _deviceContext.MutableCapabilities._supportsVulkanTaskShaderFeature = enableMeshShaderFeature;
        _deviceContext.MutableCapabilities._supportsVulkanMeshShaderFeature = enableMeshShaderFeature;
        _deviceContext.MutableCapabilities._meshShaderCapabilitySnapshot = new VulkanMeshShaderCapabilitySnapshot(
            meshShaderExtensionAdvertised,
            meshShaderExtensionRequested,
            meshShaderExtensionEnabled,
            taskShaderFeatureSupported,
            meshShaderFeatureSupported,
            meshShaderQueriesSupported,
            enableMeshShaderFeature,
            enableMeshShaderFeature,
            enableMeshShaderQueries,
            CommandTableLoaded: false,
            NegotiatedApiVersion: _deviceContext.InstanceApiVersion,
            Properties: meshShaderProperties);
        ResourceRuntime.Queries.MeshShaderQueriesEnabled = enableMeshShaderQueries;
        ResourceRuntime.Queries.HostResetAdvertised = hostQueryResetFeatureSupported;
        ResourceRuntime.Queries.PrimitivesGeneratedAdvertised = primitivesGeneratedFeatures.PrimitivesGeneratedQuery;
        ResourceRuntime.Queries.PrimitivesGeneratedEnabled = enablePrimitivesGeneratedQuery;
        ResourceRuntime.Queries.PrimitivesGeneratedNonZeroStreamsEnabled = primitivesGeneratedFeatureEnable.PrimitivesGeneratedQueryWithNonZeroStreams;

        // Load optional extension command tables before resolving backend modes that depend on them.
        LoadOptionalDeviceExtensions(
            extensionsArray,
            enableDrawIndirectCountFeature,
            _outputRuntime,
            ResourceRuntime);
        ResourceRuntime.Queries.RequestBackendContextBinding();
        ResourceRuntime.Queries.RefreshCapabilities();
        VulkanDeviceCapabilityReporter.LogVulkanDiagnosticDeviceCapabilities(
            _deviceContext,
            new VulkanDiagnosticCapabilitySnapshot(
            _frameTelemetry._diagnosticOptions.RequestDeviceFault,
            _frameTelemetry._diagnosticOptions.RequestDeviceAddressBindingReport,
            _frameTelemetry._diagnosticOptions.RequestNvDiagnosticCheckpoints,
            _frameTelemetry._diagnosticOptions.RequestNvDiagnosticsConfig,
            khrDeviceFaultExtensionAvailable,
            khrDeviceFaultExtensionEnabled,
            khrDeviceFaultFeatureSupported,
            khrDeviceFaultVendorBinaryFeatureSupported,
            khrDeviceFaultReportMaskedFeatureSupported,
            khrDeviceFaultDeviceLostOnMaskedFeatureSupported,
            khrDeviceFaultMaxReportCount,
            extDeviceFaultExtensionAvailable,
            extDeviceFaultExtensionEnabled,
            extDeviceFaultFeatureSupported,
            extDeviceFaultVendorBinaryFeatureSupported,
            deviceAddressBindingReportExtensionAvailable,
            deviceAddressBindingReportExtensionEnabled,
            deviceAddressBindingReportFeatureSupported,
            nvDiagnosticCheckpointsExtensionAvailable,
            nvDiagnosticCheckpointsExtensionEnabled,
            nvDiagnosticsConfigExtensionAvailable,
            nvDiagnosticsConfigExtensionEnabled,
            nvDiagnosticsConfigFeatureSupported));

        ResourceRuntime.Descriptors.ResolveDescriptorBackendAfterDeviceCreate(
            request.FeaturePolicy.RequestedDescriptorBackend,
            enableDescriptorIndexing,
            descriptorHeapExtensionAvailable,
            descriptorHeapDependenciesReady,
            descriptorHeapFeatureSupported,
            descriptorHeapNativeApiAvailable);
        if (enableDescriptorHeapFeature && !descriptorHeapNativeApiAvailable)
        {
            Debug.VulkanWarning(
                "[Vulkan.DescriptorHeap.Capability] feature enabled but native API loading failed: {0}",
                descriptorHeapNativeApiReason);
        }
        EVulkanCapabilityTier? explicitCapabilityTier = request.FeaturePolicy.ExplicitCapabilityTier;
        EVulkanDescriptorBackend? explicitDescriptorBackend = request.FeaturePolicy.ExplicitDescriptorBackend;
        EVulkanProgramBindingBackend? explicitProgramBindingBackend = request.FeaturePolicy.ExplicitProgramBindingBackend;
        EVulkanFoveationBackend? explicitFoveationBackend = request.FeaturePolicy.ExplicitFoveationBackend;
        EVulkanRayTracingBackend? explicitRayTracingBackend = request.FeaturePolicy.ExplicitRayTracingBackend;
        VulkanExplicitCapabilityPolicyValidator.Validate(
            new VulkanExplicitCapabilityPolicySnapshot(
            vulkan13PromotedToCore,
            vulkan14PromotedToCore,
            enableDynamicRenderingFeature,
            enableDynamicRenderingLocalReadFeature,
            enableMaintenance4Feature,
            enableMaintenance5Feature,
            enableSynchronization2Feature,
            enableTimelineSemaphoreFeature,
            enableDescriptorIndexing,
            enableBufferDeviceAddress,
            drawIndirectCountExtensionEnabled || enableDrawIndirectCountFeature,
            descriptorHeapExtensionAvailable,
            descriptorHeapDependenciesReady,
            ResourceRuntime.Descriptors._descriptorHeapNativeApiAvailable,
            SupportsDescriptorHeap,
            ResourceRuntime.Descriptors._activeDescriptorBackend,
            ResourceRuntime.Descriptors._descriptorBackendFallbackReason,
            shaderObjectFeatureSupported,
            enableFragmentShadingRateFeature,
            enableFragmentDensityMapFeature,
            accelerationStructureFeatureSupported,
            rayTracingPipelineFeatureSupported,
            rayQueryFeatureSupported,
            explicitCapabilityTier,
            explicitDescriptorBackend,
            explicitProgramBindingBackend,
            explicitFoveationBackend,
            explicitRayTracingBackend));
        if (descriptorIndexingExtensionEnabled && !enableDescriptorIndexing)
        {
            Debug.VulkanWarning(
                "[Vulkan] Descriptor indexing extension present but disabled (requested={0}, runtimeArray={1}, partiallyBound={2}, variableCount={3}, sampledImageNonUniformIndexing={4}, updateAfterBind={5}).",
                descriptorIndexingRequestedByProfile,
                _deviceContext.MutableCapabilities._supportsRuntimeDescriptorArray,
                _deviceContext.MutableCapabilities._supportsDescriptorBindingPartiallyBound,
                _deviceContext.MutableCapabilities._supportsDescriptorBindingVariableDescriptorCount,
                _deviceContext.MutableCapabilities._supportsShaderSampledImageArrayNonUniformIndexing,
                _deviceContext.MutableCapabilities._supportsDescriptorBindingUpdateAfterBind);
        }
            else if (enableDescriptorIndexing && !_deviceContext.MutableCapabilities._supportsDescriptorBindingStorageImageUpdateAfterBind)
            {
                Debug.VulkanWarning(
                "[Vulkan] Descriptor indexing enabled without storage-image update-after-bind support; storage image bindings will not use UPDATE_AFTER_BIND flags.");
            }

            ResourceRuntime.Descriptors.ValidateRequiredVulkanBindlessMaterialCapability();

            if (!enableShaderDrawParametersFeature && !shaderDrawParametersExtensionEnabled)
            {
                Debug.VulkanWarning(
                    "[Vulkan] Draw parameters support unavailable (shaderDrawParametersFeature={0}, extensionEnabled={1}). Shaders using gl_BaseVertex/gl_BaseInstance may fail.",
                    shaderDrawParametersFeatureSupported,
                    shaderDrawParametersExtensionEnabled);
            }

            if (!enableShaderOutputViewportIndexFeature || !enableShaderOutputLayerFeature)
            {
                Debug.VulkanWarning(
                    "[Vulkan] Shader viewport/layer output support incomplete (viewportIndexFeature={0}, layerFeature={1}, promotedToCore={2}). Cascaded shadow atlas rendering may fall back.",
                    shaderOutputViewportIndexFeatureSupported,
                    shaderOutputLayerFeatureSupported,
                    shaderViewportLayerPromotedToCore);
            }

            if (depthClipControlExtensionEnabled && !enableDepthClipControlFeature)
            {
                Debug.VulkanWarning(
                    "[Vulkan] {0} extension present but disabled because the depthClipControl feature bit is unavailable.",
                    VulkanDepthClipControlExt.ExtensionName);
            }

            if (!enableMultiviewFeature)
            {
                Debug.VulkanWarning(
                    "[Vulkan] Multiview support unavailable (featureSupported={0}, extensionEnabled={1}, promotedToCore={2}). Stereo single-pass multiview path will be disabled.",
                    multiviewFeatureSupported,
                    multiviewExtensionEnabled,
                    multiviewPromotedToCore);
            }

            if (nvMemoryDecompressionExtensionEnabled && !enableNvMemoryDecompression)
            {
                Debug.VulkanWarning(
                "[Vulkan] VK_NV_memory_decompression present but disabled (requested={0}, featureSupported={1}).",
                nvMemoryDecompressionRequestedByProfile,
                nvMemoryDecompressionFeatureSupported);
            }

            if (nvCopyMemoryIndirectExtensionEnabled && !enableNvCopyMemoryIndirect)
            {
                Debug.VulkanWarning(
                    "[Vulkan] VK_NV_copy_memory_indirect present but disabled (requested={0}, featureSupported={1}).",
                    nvCopyMemoryIndirectRequestedByProfile,
                    nvCopyMemoryIndirectFeatureSupported);
            }

            if (enableNvCopyMemoryIndirect && !enableBufferDeviceAddress)
            {
                Debug.VulkanWarning(
                    "[Vulkan] VK_NV_copy_memory_indirect enabled but buffer device address is unavailable; indirect copy commands will be disabled.");
            }

            if (bufferDeviceAddressRequestedBySceneDatabase && !enableBufferDeviceAddress)
            {
                Debug.VulkanWarning(
                    "[Vulkan] Scene-database bufferDeviceAddress was requested (geometryFetch={0}, bindlessMaterialTable={1}) but the feature is unavailable.",
                    request.FeaturePolicy.ActiveGeometryFetchMode,
                    request.FeaturePolicy.EnableBindlessMaterialTable);
            }

            if (!enableIndexTypeUint8Feature)
            {
                Debug.VulkanWarning(
                    "[Vulkan] UINT8 index type unsupported or disabled (featureSupported={0}, extensionEnabled={1}). Byte-sized index buffers will be skipped.",
                    indexTypeUint8FeatureSupported,
                    indexTypeUint8ExtensionEnabled);
            }

            if (!enableTimelineSemaphoreFeature)
            {
                Debug.VulkanWarning(
                    "[Vulkan] Timeline semaphores unsupported or disabled (featureSupported={0}). Renderer timeline synchronization path requires this feature.",
                    timelineSemaphoreFeatureSupported);
            }

            if (synchronization2ExtensionEnabled && !enableSynchronization2Feature)
            {
                Debug.VulkanWarning(
                    "[Vulkan] VK_KHR_synchronization2 present but disabled (featureSupported={0}). Renderer will remain on legacy barrier/submit APIs.",
                    _deviceContext.MutableCapabilities._supportsSynchronization2Feature);
            }

            if (meshShaderExtensionEnabled && !enableMeshShaderFeature)
            {
                Debug.VulkanWarning(
                    "[Vulkan] VK_EXT_mesh_shader present but disabled (taskShaderFeature={0}, meshShaderFeature={1}). Production meshlet dispatch will remain unavailable.",
                    taskShaderFeatureSupported,
                    meshShaderFeatureSupported);
            }

            if (graphicsPipelineLibraryExtensionEnabled && !enableGraphicsPipelineLibraryFeature)
            {
                Debug.VulkanWarning(
                    "[Vulkan] VK_EXT_graphics_pipeline_library present but disabled because the graphicsPipelineLibrary feature bit is unavailable.");
            }

            if (transformFeedbackExtensionEnabled && !enableTransformFeedbackFeature)
            {
                Debug.VulkanWarning(
                    "[Vulkan] {0} present but disabled because the transformFeedback feature bit is unavailable.",
                    ExtTransformFeedback.ExtensionName);
            }

            if (fragmentShadingRateExtensionEnabled)
            {
                if (enableFragmentShadingRateFeature)
                {
                    Debug.Vulkan(
                        "[Vulkan] VK_KHR_fragment_shading_rate enabled (pipeline={0}, primitive={1}, attachment={2}, attachmentTexelMin={3}x{4}, attachmentTexelMax={5}x{6}, maxFragment={7}x{8}, nonTrivialCombiner={9}, strictMultiplyCombiner={10}).",
                        pipelineFragmentShadingRateSupported,
                        primitiveFragmentShadingRateSupported,
                        attachmentFragmentShadingRateSupported,
                        fragmentShadingRateProperties.MinFragmentShadingRateAttachmentTexelSize.Width,
                        fragmentShadingRateProperties.MinFragmentShadingRateAttachmentTexelSize.Height,
                        fragmentShadingRateProperties.MaxFragmentShadingRateAttachmentTexelSize.Width,
                        fragmentShadingRateProperties.MaxFragmentShadingRateAttachmentTexelSize.Height,
                        fragmentShadingRateProperties.MaxFragmentSize.Width,
                        fragmentShadingRateProperties.MaxFragmentSize.Height,
                        fragmentShadingRateProperties.FragmentShadingRateNonTrivialCombinerOps,
                        fragmentShadingRateProperties.FragmentShadingRateStrictMultiplyCombiner);
                }
                else
                {
                    Debug.VulkanWarning(
                        "[Vulkan] VK_KHR_fragment_shading_rate present but disabled because no fragment shading-rate feature bit is available.");
                }
            }

            if (fragmentDensityMapExtensionEnabled)
            {
                if (enableFragmentDensityMapFeature)
                {
                    Debug.Vulkan(
                        "[Vulkan] VK_EXT_fragment_density_map enabled (dynamic={0}, nonSubsampledImages={1}).",
                        fragmentDensityMapDynamicSupported,
                        fragmentDensityMapNonSubsampledImagesSupported);
                }
                else
                {
                    Debug.VulkanWarning(
                        "[Vulkan] VK_EXT_fragment_density_map present but disabled because the fragmentDensityMap feature bit is unavailable.");
                }
            }

        VulkanDiagnosticCapabilitySnapshot diagnosticSnapshot = new(
            _frameTelemetry._diagnosticOptions.RequestDeviceFault,
            _frameTelemetry._diagnosticOptions.RequestDeviceAddressBindingReport,
            _frameTelemetry._diagnosticOptions.RequestNvDiagnosticCheckpoints,
            _frameTelemetry._diagnosticOptions.RequestNvDiagnosticsConfig,
            khrDeviceFaultExtensionAvailable,
            khrDeviceFaultExtensionEnabled,
            khrDeviceFaultFeatureSupported,
            khrDeviceFaultVendorBinaryFeatureSupported,
            khrDeviceFaultReportMaskedFeatureSupported,
            khrDeviceFaultDeviceLostOnMaskedFeatureSupported,
            khrDeviceFaultMaxReportCount,
            extDeviceFaultExtensionAvailable,
            extDeviceFaultExtensionEnabled,
            extDeviceFaultFeatureSupported,
            extDeviceFaultVendorBinaryFeatureSupported,
            deviceAddressBindingReportExtensionAvailable,
            deviceAddressBindingReportExtensionEnabled,
            deviceAddressBindingReportFeatureSupported,
            nvDiagnosticCheckpointsExtensionAvailable,
            nvDiagnosticCheckpointsExtensionEnabled,
            nvDiagnosticsConfigExtensionAvailable,
            nvDiagnosticsConfigExtensionEnabled,
            nvDiagnosticsConfigFeatureSupported);
        VulkanExplicitCapabilityPolicySnapshot explicitPolicy = new(
            vulkan13PromotedToCore,
            vulkan14PromotedToCore,
            enableDynamicRenderingFeature,
            enableDynamicRenderingLocalReadFeature,
            enableMaintenance4Feature,
            enableMaintenance5Feature,
            enableSynchronization2Feature,
            enableTimelineSemaphoreFeature,
            enableDescriptorIndexing,
            enableBufferDeviceAddress,
            drawIndirectCountExtensionEnabled || enableDrawIndirectCountFeature,
            descriptorHeapExtensionAvailable,
            descriptorHeapDependenciesReady,
            ResourceRuntime.Descriptors._descriptorHeapNativeApiAvailable,
            SupportsDescriptorHeap,
            ResourceRuntime.Descriptors._activeDescriptorBackend,
            ResourceRuntime.Descriptors._descriptorBackendFallbackReason,
            shaderObjectFeatureSupported,
            enableFragmentShadingRateFeature,
            enableFragmentDensityMapFeature,
            accelerationStructureFeatureSupported,
            rayTracingPipelineFeatureSupported,
            rayQueryFeatureSupported,
            request.FeaturePolicy.ExplicitCapabilityTier,
            request.FeaturePolicy.ExplicitDescriptorBackend,
            request.FeaturePolicy.ExplicitProgramBindingBackend,
            request.FeaturePolicy.ExplicitFoveationBackend,
            request.FeaturePolicy.ExplicitRayTracingBackend);

        return new VulkanLogicalDeviceBootstrapResult(
            _outputRuntime.CreatePublication(),
            ResourceRuntime.CreatePublication(),
            new VulkanLogicalDeviceBootstrapResult.CommandPublication(
                UseCoreDynamicRenderingCommands,
                UseCoreSynchronization2Commands,
                _deviceContext.MutableCapabilities._supportsDrawIndirectCount),
            new VulkanLogicalDeviceBootstrapResult.EnginePublication(
                enableMultiviewFeature,
                enableDepthClipControlFeature,
                SupportsNvMemoryDecompression,
                SupportsNvCopyMemoryIndirect),
            diagnosticSnapshot,
            explicitPolicy,
            new VulkanLayeredShadowCapabilityRequest(
                enableMultiViewport,
                enableShaderOutputViewportIndexFeature,
                enableShaderOutputLayerFeature));

    }

    /// <summary>
    /// Loads optional device extension handles after device creation.
    /// </summary>
    private void LoadOptionalDeviceExtensions(
        string[] enabledExtensions,
        bool enableDrawIndirectCountFeature,
        VulkanLogicalDeviceOutputPolicyState output,
        VulkanLogicalDeviceResourcePublicationBuilder resources)
    {
        VulkanDeviceContext _deviceContext = this;
        VulkanLogicalDeviceOutputPolicyState _outputRuntime = output;
        VulkanLogicalDeviceResourcePublicationBuilder ResourceRuntime = resources;
        _deviceContext.LoadAndFinalizeExtensionFunctions(
            Api!,
            new VulkanDeviceExtensionLoadRequest(
                RequireKhrDynamicRenderingCommands:
                    !UseCoreDynamicRenderingCommands ||
                    _outputRuntime._streamlineFrameGenerationProvisioned,
                RequireKhrSynchronization2Commands: !UseCoreSynchronization2Commands,
                EnableCoreDrawIndirectCount: enableDrawIndirectCountFeature));
        bool descriptorIndexingExtensionLoaded = enabledExtensions.Contains("VK_EXT_descriptor_indexing");

        if (enabledExtensions.Contains("VK_KHR_dynamic_rendering") &&
            (!UseCoreDynamicRenderingCommands || _outputRuntime._streamlineFrameGenerationProvisioned))
        {
            if (_khrDynamicRendering is not null)
            {
                Debug.Vulkan(
                    "[Vulkan] VK_KHR_dynamic_rendering extension command table loaded for Vulkan instance API {0}.",
                    VulkanDeviceContext.FormatVulkanApiVersion(_deviceContext.InstanceApiVersion));
            }
            else
            {
                Debug.VulkanWarning("[Vulkan] Failed to load VK_KHR_dynamic_rendering extension command table.");
                _deviceContext.MutableCapabilities._supportsDynamicRendering = false;
            }
        }

        if (enabledExtensions.Contains("VK_KHR_synchronization2") && !UseCoreSynchronization2Commands)
        {
            if (_khrSynchronization2 is not null)
            {
                Debug.Vulkan(
                    "[Vulkan] VK_KHR_synchronization2 extension command table loaded for Vulkan instance API {0}.",
                    VulkanDeviceContext.FormatVulkanApiVersion(_deviceContext.InstanceApiVersion));
            }
            else
            {
                Debug.VulkanWarning("[Vulkan] Failed to load VK_KHR_synchronization2 extension command table.");
                _deviceContext.MutableCapabilities._supportsSynchronization2 = false;
            }
        }

        bool indirectCountCoreFeaturesReady =
            _deviceContext.MutableCapabilities._supportsMultiDrawIndirect &&
            _deviceContext.MutableCapabilities._supportsDrawIndirectFirstInstance;
        if (enableDrawIndirectCountFeature && indirectCountCoreFeaturesReady)
        {
            _deviceContext.MutableCapabilities._usesCoreDrawIndirectCountCommands = true;
            _deviceContext.MutableCapabilities._supportsDrawIndirectCount = true;
            Debug.Vulkan("[Vulkan] Vulkan 1.2 core indirect-count drawing enabled with required core indirect features.");
        }
        // Vulkan 1.1 and older expose the command through VK_KHR_draw_indirect_count.
        else if (enabledExtensions.Contains("VK_KHR_draw_indirect_count"))
        {
            if (_khrDrawIndirectCount is not null)
            {
                _deviceContext.MutableCapabilities._supportsDrawIndirectCount = indirectCountCoreFeaturesReady;
                if (_deviceContext.MutableCapabilities._supportsDrawIndirectCount)
                {
                    Debug.Vulkan("[Vulkan] VK_KHR_draw_indirect_count extension loaded with required core indirect features.");
                }
                else
                {
                    Debug.VulkanWarning(
                        "[Vulkan] VK_KHR_draw_indirect_count loaded but disabled for engine submission " +
                        "because required core features are unavailable (multiDrawIndirect={0}, drawIndirectFirstInstance={1}).",
                        _deviceContext.MutableCapabilities._supportsMultiDrawIndirect,
                        _deviceContext.MutableCapabilities._supportsDrawIndirectFirstInstance);
                }
            }
            else
            {
                Debug.VulkanWarning("[Vulkan] Failed to load VK_KHR_draw_indirect_count extension handle.");
                _deviceContext.MutableCapabilities._supportsDrawIndirectCount = false;
            }
        }

        if (enabledExtensions.Contains(ExtMeshShader.ExtensionName))
        {
            if (_deviceContext.MutableCapabilities._supportsVulkanTaskShaderFeature &&
                _deviceContext.MutableCapabilities._supportsVulkanMeshShaderFeature &&
                _extMeshShader is not null)
            {
                _deviceContext.MutableCapabilities._supportsVulkanMeshTaskIndirectCount = true;
                Debug.Vulkan("[Vulkan] VK_EXT_mesh_shader extension loaded successfully for indirect-count mesh task dispatch.");
            }
            else
            {
                Debug.VulkanWarning(
                    "[Vulkan] Failed to load VK_EXT_mesh_shader for production dispatch (taskFeature={0}, meshFeature={1}).",
                    _deviceContext.MutableCapabilities._supportsVulkanTaskShaderFeature,
                    _deviceContext.MutableCapabilities._supportsVulkanMeshShaderFeature);
                _deviceContext.MutableCapabilities._supportsVulkanMeshTaskIndirectCount = false;
            }
        }

        VulkanMeshShaderCapabilitySnapshot meshShaderCapability = _deviceContext.MutableCapabilities._meshShaderCapabilitySnapshot;
        _deviceContext.MutableCapabilities._meshShaderCapabilitySnapshot = meshShaderCapability with
        {
            CommandTableLoaded = _extMeshShader is not null,
        };

        if (enabledExtensions.Contains(ExtTransformFeedback.ExtensionName))
        {
            if (_deviceContext.MutableCapabilities._supportsTransformFeedback &&
                _extTransformFeedback is not null)
            {
                Debug.Vulkan(
                    "[Vulkan] {0} loaded successfully (buffers={1}, maxBufferSize={2}, queries={3}, draw={4}, geometryStreams={5}).",
                    ExtTransformFeedback.ExtensionName,
                    _deviceContext.MutableCapabilities._transformFeedbackProperties.MaxTransformFeedbackBuffers,
                    _deviceContext.MutableCapabilities._transformFeedbackProperties.MaxTransformFeedbackBufferSize,
                    _deviceContext.MutableCapabilities._supportsTransformFeedbackQueries,
                    _deviceContext.MutableCapabilities._supportsTransformFeedbackDraw,
                    _deviceContext.MutableCapabilities._supportsTransformFeedbackGeometryStreams);
            }
            else
            {
                Debug.VulkanWarning(
                    "[Vulkan] Failed to load {0} extension handle or feature was disabled.",
                    ExtTransformFeedback.ExtensionName);
                _deviceContext.MutableCapabilities._supportsTransformFeedback = false;
                _deviceContext.MutableCapabilities._supportsTransformFeedbackGeometryStreams = false;
                _deviceContext.MutableCapabilities._supportsTransformFeedbackQueries = false;
                _deviceContext.MutableCapabilities._supportsTransformFeedbackDraw = false;
                _deviceContext.MutableCapabilities._transformFeedbackProperties = default;
            }
        }

        if (enabledExtensions.Contains("VK_KHR_external_memory_win32"))
        {
            if (_khrExternalMemoryWin32 is not null)
            {
                _deviceContext.MutableCapabilities._supportsExternalMemoryWin32 = true;
                Debug.Vulkan("[Vulkan] VK_KHR_external_memory_win32 extension loaded successfully.");
            }
            else
            {
                Debug.VulkanWarning("[Vulkan] Failed to load VK_KHR_external_memory_win32 extension handle.");
                _deviceContext.MutableCapabilities._supportsExternalMemoryWin32 = false;
            }
        }

        if (enabledExtensions.Contains("VK_KHR_external_semaphore_win32"))
        {
            if (_khrExternalSemaphoreWin32 is not null)
            {
                _deviceContext.MutableCapabilities._supportsExternalSemaphoreWin32 = true;
                Debug.Vulkan("[Vulkan] VK_KHR_external_semaphore_win32 extension loaded successfully.");
            }
            else
            {
                Debug.VulkanWarning("[Vulkan] Failed to load VK_KHR_external_semaphore_win32 extension handle.");
                _deviceContext.MutableCapabilities._supportsExternalSemaphoreWin32 = false;
            }
        }

        if (enabledExtensions.Contains("VK_NV_memory_decompression") && _deviceContext.MutableCapabilities._supportsNvMemoryDecompression)
        {
            if (_nvMemoryDecompression is not null)
            {
                _deviceContext.MutableCapabilities._supportsNvMemoryDecompression = true;
                Debug.Vulkan(
                    "[Vulkan] VK_NV_memory_decompression loaded successfully (methodsMask=0x{0:X}, maxIndirectCount={1}).",
                    _deviceContext.MutableCapabilities._nvMemoryDecompressionMethods,
                    _deviceContext.MutableCapabilities._nvMaxMemoryDecompressionIndirectCount);
            }
            else
            {
                Debug.VulkanWarning("[Vulkan] Failed to load VK_NV_memory_decompression extension handle.");
                _deviceContext.MutableCapabilities._supportsNvMemoryDecompression = false;
                _deviceContext.MutableCapabilities._nvMemoryDecompressionMethods = 0;
                _deviceContext.MutableCapabilities._nvMaxMemoryDecompressionIndirectCount = 0;
            }
        }

        if (enabledExtensions.Contains("VK_NV_copy_memory_indirect") && _deviceContext.MutableCapabilities._supportsNvCopyMemoryIndirect)
        {
            if (_nvCopyMemoryIndirect is not null)
            {
                _deviceContext.MutableCapabilities._supportsNvCopyMemoryIndirect = true;
                Debug.Vulkan(
                    "[Vulkan] VK_NV_copy_memory_indirect loaded successfully (supportedQueuesMask=0x{0:X}).",
                    _deviceContext.MutableCapabilities._nvCopyMemoryIndirectSupportedQueues);
            }
            else
            {
                Debug.VulkanWarning("[Vulkan] Failed to load VK_NV_copy_memory_indirect extension handle.");
                _deviceContext.MutableCapabilities._supportsNvCopyMemoryIndirect = false;
                _deviceContext.MutableCapabilities._nvCopyMemoryIndirectSupportedQueues = 0;
            }
        }

        VulkanDeviceFaultFacility deviceFaultFacility = _deviceContext.DeviceFaultFacility;
        if (enabledExtensions.Contains(KhrDeviceFaultExtensionName) && deviceFaultFacility.SupportsKhrDeviceFault)
        {
            if (!_deviceContext.TryLoadKhrDeviceFaultCommandTable(
                    Api!,
                    out nint reportsAddress,
                    out nint debugInfoAddress))
            {
                Debug.VulkanWarning(
                    "[VulkanDiag] KHR advertised but function pointer unavailable: reports=0x{0:X} debugInfo=0x{1:X}.",
                    reportsAddress,
                    debugInfoAddress);
                deviceFaultFacility.PublishKhrSupport(
                    supportsDeviceFault: false,
                    supportsVendorBinary: false,
                    supportsReportMasked: false,
                    supportsDeviceLostOnMasked: false,
                    deviceFaultFacility.KhrDeviceFaultMaxReportCount);
            }
            else
            {
                Debug.Vulkan(
                    "[VulkanDiag] DeviceFaultKHR active reports=0x{0:X} debugInfo=0x{1:X} vendorBinary={2} maskedReports={3} lostOnMasked={4}.",
                    reportsAddress,
                    debugInfoAddress,
                    deviceFaultFacility.SupportsKhrDeviceFaultVendorBinary,
                    deviceFaultFacility.SupportsKhrDeviceFaultReportMasked,
                    deviceFaultFacility.SupportsKhrDeviceFaultDeviceLostOnMasked);
            }
        }

        if (enabledExtensions.Contains(ExtDeviceFaultExtensionName) && deviceFaultFacility.SupportsExtDeviceFault)
        {
            if (_deviceContext.ExtensionFunctions.ExtDeviceFault is not null)
            {
                Debug.Vulkan(
                    "[VulkanDiag] DeviceFaultEXT compatibility active extension={0} vendorBinary={1}.",
                    ExtDeviceFaultExtensionName,
                    deviceFaultFacility.SupportsExtDeviceFaultVendorBinary);
            }
            else
            {
                Debug.VulkanWarning("[VulkanDiag] Failed to load {0} extension handle.", ExtDeviceFaultExtensionName);
                _deviceContext.DeviceFaultFacility.PublishExtSupport(
                    supportsDeviceFault: false,
                    supportsVendorBinary: false);
            }
        }

        if (enabledExtensions.Contains(NvDeviceDiagnosticCheckpointsExtensionName) && _deviceContext.MutableCapabilities._supportsNvDiagnosticCheckpoints)
        {
            if (_deviceContext.ExtensionFunctions.NvDeviceDiagnosticCheckpoints is not null)
            {
                Debug.Vulkan("[VulkanDiag] {0} loaded successfully.", NvDeviceDiagnosticCheckpointsExtensionName);
            }
            else
            {
                Debug.VulkanWarning("[VulkanDiag] Failed to load {0} extension handle.", NvDeviceDiagnosticCheckpointsExtensionName);
                _deviceContext.MutableCapabilities._supportsNvDiagnosticCheckpoints = false;
            }
        }

        if (_deviceContext.MutableCapabilities._supportsNvCopyMemoryIndirect && !_deviceContext.MutableCapabilities._supportsBufferDeviceAddress)
        {
            _deviceContext.MutableCapabilities._supportsNvCopyMemoryIndirect = false;
            _deviceContext.MutableCapabilities._nvCopyMemoryIndirectSupportedQueues = 0;
        }

        // Resolve only after core/extension command tables have finalized the effective
        // dynamic-rendering capability. Resolving earlier could cache legacy mode before
        // VK_KHR_dynamic_rendering command loading completed.
        _outputRuntime.ResolveRenderTargetMode(_deviceContext);
        Debug.Vulkan(
            "[Vulkan] Render target mode: requested={0} resolved={1} dynamicRenderingFeature={2}. Override with {3}=Auto|DynamicRendering|LegacyRenderPass.",
            _outputRuntime._requestedRenderTargetMode,
            UseDynamicRenderingRenderTargets ? "DynamicRendering" : "LegacyRenderPass",
            _deviceContext.MutableCapabilities._supportsDynamicRendering,
            VulkanRenderTargetModeEnvVar);

        ResourceRuntime.PipelineManager.CreatePipelineCache();

        if (descriptorIndexingExtensionLoaded && _deviceContext.MutableCapabilities._supportsDescriptorIndexing)
            Debug.Vulkan("[Vulkan] VK_EXT_descriptor_indexing enabled for descriptor update-after-bind support.");
    }

    private const string VulkanRenderTargetModeEnvVar = XREngineEnvironmentVariables.VkRenderTargetMode;

    private bool UseCoreDynamicRenderingCommands => InstanceApiVersion >= Vk.Version13;
    private bool UseCoreSynchronization2Commands => InstanceApiVersion >= Vk.Version13;
    private bool UseDynamicRenderingRenderTargets => MutableCapabilities._useDynamicRenderingRenderTargets;
    internal bool SupportsDescriptorHeap => MutableCapabilities._supportsDescriptorHeap;
    internal bool SupportsNvMemoryDecompression =>
        MutableCapabilities._supportsNvMemoryDecompression &&
        ExtensionFunctions.NvMemoryDecompression is not null;
    internal bool SupportsNvCopyMemoryIndirect =>
        MutableCapabilities._supportsNvCopyMemoryIndirect &&
        ExtensionFunctions.NvCopyMemoryIndirect is not null;
    internal bool SupportsDepthClipControl => MutableCapabilities._supportsDepthClipControl;
    private KhrDrawIndirectCount? _khrDrawIndirectCount => ExtensionFunctions.KhrDrawIndirectCount;
    private KhrDynamicRendering? _khrDynamicRendering => ExtensionFunctions.KhrDynamicRendering;
    private KhrSynchronization2? _khrSynchronization2 => ExtensionFunctions.KhrSynchronization2;
    private ExtMeshShader? _extMeshShader => ExtensionFunctions.ExtMeshShader;
    private ExtTransformFeedback? _extTransformFeedback => ExtensionFunctions.ExtTransformFeedback;
    private KhrExternalMemoryWin32? _khrExternalMemoryWin32 => ExtensionFunctions.KhrExternalMemoryWin32;
    private KhrExternalSemaphoreWin32? _khrExternalSemaphoreWin32 => ExtensionFunctions.KhrExternalSemaphoreWin32;
    private NVMemoryDecompression? _nvMemoryDecompression => ExtensionFunctions.NvMemoryDecompression;
    private NVCopyMemoryIndirect? _nvCopyMemoryIndirect => ExtensionFunctions.NvCopyMemoryIndirect;

    private bool QuerySwapchainMaintenance1FeatureSupport()
    {
        PhysicalDeviceSwapchainMaintenance1FeaturesEXT supportedFeatures = new()
        {
            SType = StructureType.PhysicalDeviceSwapchainMaintenance1FeaturesExt,
        };
        PhysicalDeviceFeatures2 features = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &supportedFeatures,
        };
        Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &features);
        return supportedFeatures.SwapchainMaintenance1;
    }

    private void PopulateStreamlineRequiredFeatures<TFeatures>(
        ref TFeatures requestedFeatures,
        in TFeatures supportedFeatures,
        string[] featureNames,
        string featureGroup) where TFeatures : struct
    {
        List<string> unknownFeatures = [];
        List<string> unsupportedFeatures = [];
        foreach (string featureName in featureNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal))
        {
            string fieldName = char.ToUpperInvariant(featureName[0]) + featureName[1..];
            FieldInfo? field = typeof(TFeatures).GetField(fieldName, BindingFlags.Public | BindingFlags.Instance)
                ?? typeof(TFeatures).GetField(featureName, BindingFlags.Public | BindingFlags.Instance);
            if (field is null)
            {
                unknownFeatures.Add(featureName);
                continue;
            }

            object boxedSupported = supportedFeatures;
            if (!TryReadBooleanFeatureValue(field.GetValue(boxedSupported), out bool supported) || !supported)
            {
                unsupportedFeatures.Add(featureName);
                continue;
            }

            object boxedRequested = requestedFeatures;
            field.SetValue(boxedRequested, CreateBooleanFeatureValue(field.FieldType));
            requestedFeatures = (TFeatures)boxedRequested;
        }

        if (unknownFeatures.Count > 0)
            throw new InvalidOperationException($"Streamline requested unknown {featureGroup} feature fields: {string.Join(", ", unknownFeatures)}.");
        if (unsupportedFeatures.Count > 0)
            throw new NotSupportedException($"The selected Vulkan device does not support Streamline-required {featureGroup} features: {string.Join(", ", unsupportedFeatures)}.");
    }

    private static bool TryReadBooleanFeatureValue(object? rawValue, out bool value)
    {
        switch (rawValue)
        {
            case bool boolValue:
                value = boolValue;
                return true;
            case uint uintValue:
                value = uintValue != 0;
                return true;
            case int intValue:
                value = intValue != 0;
                return true;
            case byte byteValue:
                value = byteValue != 0;
                return true;
            case Bool32 bool32Value:
                value = bool32Value;
                return true;
            case null:
                value = false;
                return false;
        }

        Type valueType = rawValue.GetType();
        object? nestedValue = valueType.GetField("Value", BindingFlags.Public | BindingFlags.Instance)?.GetValue(rawValue)
            ?? valueType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)?.GetValue(rawValue);
        if (nestedValue is not null)
            return TryReadBooleanFeatureValue(nestedValue, out value);
        value = false;
        return false;
    }

    private static object CreateBooleanFeatureValue(Type fieldType)
    {
        if (fieldType == typeof(bool))
            return true;
        if (fieldType == typeof(uint))
            return 1u;
        if (fieldType == typeof(int))
            return 1;
        if (fieldType == typeof(byte))
            return (byte)1;
        if (fieldType == typeof(Bool32))
            return new Bool32(true);

        object instance = Activator.CreateInstance(fieldType)
            ?? throw new InvalidOperationException($"Could not construct Vulkan feature field type '{fieldType.FullName}'.");
        FieldInfo? valueField = fieldType.GetField("Value", BindingFlags.Public | BindingFlags.Instance);
        if (valueField is not null)
        {
            valueField.SetValue(instance, CreateBooleanFeatureValue(valueField.FieldType));
            return instance;
        }
        PropertyInfo? valueProperty = fieldType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
        if (valueProperty?.CanWrite == true)
        {
            valueProperty.SetValue(instance, CreateBooleanFeatureValue(valueProperty.PropertyType));
            return instance;
        }
        throw new InvalidOperationException(
            $"Unsupported Vulkan feature field type '{fieldType.FullName}' in Streamline requirement translation.");
    }

    private static uint AppendRequiredQueues(
        Dictionary<uint, uint> requestedQueueCounts,
        QueueFamilyProperties[] queueFamilies,
        uint familyIndex,
        uint additionalCount,
        string queueKind)
    {
        uint firstIndex = requestedQueueCounts.GetValueOrDefault(familyIndex);
        if (additionalCount == 0)
            return firstIndex;
        uint requestedCount = checked(firstIndex + additionalCount);
        uint availableCount = queueFamilies[familyIndex].QueueCount;
        if (requestedCount > availableCount)
        {
            throw new NotSupportedException(
                $"Streamline requires {additionalCount} additional {queueKind} queue(s) beginning at index {firstIndex} in Vulkan queue family {familyIndex}, " +
                $"but that family exposes only {availableCount} queue(s). Runtime toggling requires recreating the renderer on a compatible device.");
        }
        requestedQueueCounts[familyIndex] = requestedCount;
        return firstIndex;
    }

    private static uint FindOpticalFlowQueueFamily(QueueFamilyProperties[] queueFamilies)
    {
        const QueueFlags opticalFlowBitNv = (QueueFlags)0x00000100;
        for (uint index = 0; index < queueFamilies.Length; index++)
            if ((queueFamilies[index].QueueFlags & opticalFlowBitNv) != 0)
                return index;
        throw new NotSupportedException(
            "Streamline DLSS-G requested a native Vulkan optical-flow queue, but the selected device exposes no VK_QUEUE_OPTICAL_FLOW_BIT_NV queue family.");
    }

    private static bool TryUseGranularOpenXrStreamlineFeatureChain(
        IReadOnlyList<string> vulkan12Features,
        IReadOnlyList<string> vulkan13Features,
        out string failureReason)
    {
        for (int index = 0; index < vulkan12Features.Count; index++)
        {
            string featureName = vulkan12Features[index];
            if (string.IsNullOrWhiteSpace(featureName) ||
                featureName is "timelineSemaphore" or "descriptorIndexing" or "bufferDeviceAddress")
            {
                continue;
            }
            failureReason =
                $"Streamline-required Vulkan 1.2 feature '{featureName}' has no granular OpenXR device-chain representation.";
            return false;
        }
        for (int index = 0; index < vulkan13Features.Count; index++)
        {
            string featureName = vulkan13Features[index];
            if (string.IsNullOrWhiteSpace(featureName) ||
                featureName is "dynamicRendering" or "synchronization2" or "maintenance4" or
                    "pipelineCreationCacheControl" or "privateData")
            {
                continue;
            }
            failureReason =
                $"Streamline-required Vulkan 1.3 feature '{featureName}' has no granular OpenXR device-chain representation.";
            return false;
        }
        failureReason = string.Empty;
        return true;
    }

    private sealed class VulkanLogicalDeviceDiagnosticPolicyState(VulkanDiagnosticOptions options)
    {
        internal VulkanDiagnosticOptions _diagnosticOptions = options;
    }

    private sealed class VulkanLogicalDeviceOutputPolicyState
    {
        private readonly VulkanLogicalDeviceBootstrapRequest.OutputRequirements _requirements;
        private readonly VulkanLogicalDeviceBootstrapRequest.StreamlineRequirements _streamline;
        internal bool _streamlineDlssProvisioned;
        internal bool _streamlineFrameGenerationProvisioned;
        internal string[] _streamlineRequiredDeviceExtensions = [];
        internal string[] _streamlineRequiredFeatures12 = [];
        internal string[] _streamlineRequiredFeatures13 = [];
        internal NvidiaDlssManager.Native.StreamlineQueueRequirements _streamlineQueueRequirements;
        internal uint _streamlineGraphicsQueueFamily;
        internal uint _streamlineGraphicsQueueIndex;
        internal uint _streamlineComputeQueueFamily;
        internal uint _streamlineComputeQueueIndex;
        internal uint _streamlineOpticalFlowQueueFamily;
        internal uint _streamlineOpticalFlowQueueIndex;
        internal EVulkanRenderTargetMode _requestedRenderTargetMode;
        internal bool Maintenance1Enabled;
        internal bool ObsExternalSharingValidated;
        internal bool UseDynamicRenderingRenderTargets;

        internal VulkanLogicalDeviceOutputPolicyState(
            VulkanLogicalDeviceBootstrapRequest.OutputRequirements requirements,
            VulkanLogicalDeviceBootstrapRequest.StreamlineRequirements streamline)
        {
            _requirements = requirements;
            _streamline = streamline;
            Apply(streamline.Active);
            _requestedRenderTargetMode = requirements.RequestedRenderTargetMode;
        }

        internal VulkanLogicalDeviceOutputPolicyState TargetPolicy => this;
        internal VulkanLogicalDeviceOutputPolicyState Desktop => this;
        internal bool RequiresPresentQueue => _requirements.RequiresPresentQueue;

        internal void UsePrecomputedRequirements(bool includeDlss, bool includeFrameGeneration)
        {
            VulkanLogicalDeviceBootstrapRequest.StreamlineRequirementSet selected =
                includeFrameGeneration
                    ? _streamline.Active
                    : includeDlss
                        ? _streamline.WithoutFrameGeneration
                        : _streamline.Disabled;
            if (selected.DlssProvisioned != includeDlss ||
                selected.FrameGenerationProvisioned != includeFrameGeneration)
            {
                throw new InvalidOperationException(
                    "The composition root did not provide the requested precomputed Streamline fallback.");
            }
            Apply(selected);
        }

        private void Apply(VulkanLogicalDeviceBootstrapRequest.StreamlineRequirementSet selected)
        {
            _streamlineDlssProvisioned = selected.DlssProvisioned;
            _streamlineFrameGenerationProvisioned = selected.FrameGenerationProvisioned;
            _streamlineRequiredDeviceExtensions = selected.RequiredDeviceExtensions;
            _streamlineRequiredFeatures12 = selected.RequiredFeatures12;
            _streamlineRequiredFeatures13 = selected.RequiredFeatures13;
            _streamlineQueueRequirements = selected.QueueRequirements;
        }

        internal void ValidateObsHookDeviceCompatibility(
            VulkanDeviceContext deviceContext,
            HashSet<string> availableExtensions,
            string[] enabledExtensions)
        {
            _ = deviceContext;
            if (!_requirements.ValidateObsExternalSharing)
                return;
            ObsExternalSharingValidated =
                availableExtensions.Contains("VK_KHR_external_memory_win32") &&
                enabledExtensions.Contains("VK_KHR_external_memory_win32", StringComparer.Ordinal);
            if (_requirements.RequireObsExternalSharing && !ObsExternalSharingValidated)
            {
                throw new InvalidOperationException(
                    "Required OBS Vulkan external-memory sharing is unavailable on the selected logical device.");
            }
        }

        internal void ResolveRenderTargetMode(VulkanDeviceContext deviceContext)
        {
            bool supportsDynamicRendering = deviceContext.MutableCapabilities._supportsDynamicRendering;
            if (_requestedRenderTargetMode == EVulkanRenderTargetMode.DynamicRendering && !supportsDynamicRendering)
            {
                throw new InvalidOperationException(
                    $"Vulkan dynamic rendering was explicitly requested, but VK_KHR_dynamic_rendering/Vulkan 1.3 dynamicRendering is unavailable.");
            }
            UseDynamicRenderingRenderTargets = _requestedRenderTargetMode switch
            {
                EVulkanRenderTargetMode.LegacyRenderPass => false,
                EVulkanRenderTargetMode.DynamicRendering => true,
                _ => supportsDynamicRendering,
            };
            deviceContext.MutableCapabilities._useDynamicRenderingRenderTargets =
                UseDynamicRenderingRenderTargets;
        }

        internal VulkanLogicalDeviceBootstrapResult.OutputPublication CreatePublication()
            => new(
                _streamlineDlssProvisioned,
                _streamlineFrameGenerationProvisioned,
                _streamlineGraphicsQueueFamily,
                _streamlineGraphicsQueueIndex,
                _streamlineComputeQueueFamily,
                _streamlineComputeQueueIndex,
                _streamlineOpticalFlowQueueFamily,
                _streamlineOpticalFlowQueueIndex,
                _streamlineRequiredDeviceExtensions,
                _streamlineRequiredFeatures12,
                _streamlineRequiredFeatures13,
                _streamlineQueueRequirements,
                Maintenance1Enabled,
                _requestedRenderTargetMode,
                UseDynamicRenderingRenderTargets,
                ObsExternalSharingValidated);
    }

    private sealed class VulkanLogicalDeviceResourcePublicationBuilder
    {
        internal VulkanLogicalDeviceResourcePublicationBuilder(
            VulkanDeviceContext deviceContext,
            VulkanLogicalDeviceBootstrapRequest.FeaturePolicyFacts featurePolicy)
        {
            Queries = new QueryState();
            Descriptors = new DescriptorState(deviceContext, featurePolicy);
            PipelineManager = new PipelineState();
        }

        internal QueryState Queries { get; }
        internal DescriptorState Descriptors { get; }
        internal PipelineState PipelineManager { get; }

        internal VulkanLogicalDeviceBootstrapResult.ResourcePublication CreatePublication()
            => new(
                Queries.CreatePublication(),
                Descriptors.CreatePublication(),
                PipelineManager._supportsPipelineCreationCacheControl,
                PipelineManager.CreateRequested);

        internal sealed class QueryState
        {
            internal bool OcclusionPreciseAdvertised;
            internal bool OcclusionPreciseEnabled;
            internal bool PipelineStatisticsAdvertised;
            internal bool PipelineStatisticsEnabled;
            internal bool InheritedQueriesAdvertised;
            internal bool InheritedQueriesEnabled;
            internal bool MeshShaderQueriesEnabled;
            internal bool HostResetAdvertised;
            internal bool PrimitivesGeneratedAdvertised;
            internal bool PrimitivesGeneratedEnabled;
            internal bool PrimitivesGeneratedNonZeroStreamsEnabled;
            internal void RequestBackendContextBinding() { }
            internal void RefreshCapabilities() { }
            internal VulkanLogicalDeviceBootstrapResult.QueryPublication CreatePublication()
                => new(
                    OcclusionPreciseAdvertised,
                    OcclusionPreciseEnabled,
                    PipelineStatisticsAdvertised,
                    PipelineStatisticsEnabled,
                    InheritedQueriesAdvertised,
                    InheritedQueriesEnabled,
                    MeshShaderQueriesEnabled,
                    HostResetAdvertised,
                    PrimitivesGeneratedAdvertised,
                    PrimitivesGeneratedEnabled,
                    PrimitivesGeneratedNonZeroStreamsEnabled);
        }

        internal sealed class PipelineState
        {
            internal bool _supportsPipelineCreationCacheControl;
            internal bool CreateRequested;
            internal void CreatePipelineCache() => CreateRequested = true;
        }

        internal sealed class DescriptorState(
            VulkanDeviceContext deviceContext,
            VulkanLogicalDeviceBootstrapRequest.FeaturePolicyFacts featurePolicy)
        {
            internal bool _descriptorHeapFeatureSupported;
            internal bool _descriptorHeapCaptureReplaySupported;
            internal bool _descriptorHeapShaderUntypedPointersAvailable;
            internal bool _descriptorHeapNativeApiAvailable;
            internal PhysicalDeviceDescriptorHeapPropertiesEXTNative _descriptorHeapProperties;
            internal EVulkanDescriptorBackend _activeDescriptorBackend = EVulkanDescriptorBackend.DescriptorSets;
            internal string _descriptorBackendFallbackReason = string.Empty;
            private bool _descriptorIndexingEnabled;
            private bool _descriptorHeapExtensionAvailable;
            private bool _descriptorHeapDependenciesReady;
            private string _descriptorHeapNativeApiReason = string.Empty;
            private VulkanDescriptorHeapNativeFunctions? _descriptorHeapNativeFunctions;

            internal void QueryDescriptorHeapCapabilities(
                bool descriptorHeapExtensionAvailable,
                bool shaderUntypedPointersAvailable,
                out bool descriptorHeapFeatureSupported,
                out bool descriptorHeapCaptureReplaySupported,
                out PhysicalDeviceDescriptorHeapPropertiesEXTNative descriptorHeapProperties)
            {
                _descriptorHeapShaderUntypedPointersAvailable = shaderUntypedPointersAvailable;
                descriptorHeapFeatureSupported = false;
                descriptorHeapCaptureReplaySupported = false;
                descriptorHeapProperties = default;
                if (!descriptorHeapExtensionAvailable)
                    return;
                PhysicalDeviceDescriptorHeapFeaturesEXTNative features = new()
                {
                    SType = VulkanDescriptorHeapExt.PhysicalDeviceDescriptorHeapFeaturesSType,
                };
                PhysicalDeviceFeatures2 features2 = new()
                {
                    SType = StructureType.PhysicalDeviceFeatures2,
                    PNext = &features,
                };
                deviceContext.Api.GetPhysicalDeviceFeatures2(deviceContext.PhysicalDevice, &features2);
                descriptorHeapFeatureSupported = features.DescriptorHeap;
                descriptorHeapCaptureReplaySupported = features.DescriptorHeapCaptureReplay;
                PhysicalDeviceDescriptorHeapPropertiesEXTNative properties = new()
                {
                    SType = VulkanDescriptorHeapExt.PhysicalDeviceDescriptorHeapPropertiesSType,
                };
                PhysicalDeviceProperties2 properties2 = new()
                {
                    SType = StructureType.PhysicalDeviceProperties2,
                    PNext = &properties,
                };
                deviceContext.Api.GetPhysicalDeviceProperties2(deviceContext.PhysicalDevice, &properties2);
                descriptorHeapProperties = properties;
            }

            internal bool TryInitializeDescriptorHeapNativeApi(out string reason)
            {
                VulkanDescriptorHeapNativeFunctions functions = new();
                if (!functions.TryLoad(
                        deviceContext.Api,
                        deviceContext.Instance,
                        deviceContext.Device,
                        out reason))
                {
                    _descriptorHeapNativeApiReason = reason;
                    return false;
                }
                _descriptorHeapNativeFunctions = functions;
                _descriptorHeapNativeApiReason = string.Empty;
                _descriptorHeapNativeApiAvailable = true;
                return true;
            }

            internal void ResolveDescriptorBackendAfterDeviceCreate(
                EVulkanDescriptorBackend requestedBackend,
                bool descriptorIndexingEnabled,
                bool descriptorHeapExtensionAvailable,
                bool descriptorHeapDependenciesReady,
                bool descriptorHeapFeatureSupported,
                bool descriptorHeapNativeApiAvailable)
            {
                _descriptorIndexingEnabled = descriptorIndexingEnabled;
                _descriptorHeapExtensionAvailable = descriptorHeapExtensionAvailable;
                _descriptorHeapDependenciesReady = descriptorHeapDependenciesReady;
                _activeDescriptorBackend = EVulkanDescriptorBackend.DescriptorSets;
                _descriptorBackendFallbackReason = string.Empty;
                if (requestedBackend == EVulkanDescriptorBackend.DescriptorHeap &&
                    descriptorHeapExtensionAvailable &&
                    descriptorHeapDependenciesReady &&
                    descriptorHeapFeatureSupported &&
                    descriptorHeapNativeApiAvailable)
                {
                    _activeDescriptorBackend = EVulkanDescriptorBackend.DescriptorHeap;
                    _descriptorBackendFallbackReason = "Descriptor heap device support is ready for resource-authority storage resolution.";
                    return;
                }
                if (requestedBackend == EVulkanDescriptorBackend.DescriptorHeap)
                    _descriptorBackendFallbackReason = "Descriptor heap device requirements are incomplete.";
                if (descriptorIndexingEnabled && requestedBackend != EVulkanDescriptorBackend.DescriptorSets)
                    _activeDescriptorBackend = EVulkanDescriptorBackend.DescriptorIndexing;
            }

            internal void ValidateRequiredVulkanBindlessMaterialCapability()
            {
                if (featurePolicy.RequireBindlessMaterialTable && !_descriptorIndexingEnabled)
                {
                    throw new NotSupportedException(
                        "The required Vulkan bindless-material path needs descriptor indexing.");
                }
            }

            internal VulkanLogicalDeviceBootstrapResult.DescriptorPublication CreatePublication()
                => new(
                    _descriptorIndexingEnabled,
                    _descriptorHeapExtensionAvailable,
                    _descriptorHeapDependenciesReady,
                    _descriptorHeapFeatureSupported,
                    _descriptorHeapCaptureReplaySupported,
                    _descriptorHeapShaderUntypedPointersAvailable,
                    _descriptorHeapNativeApiAvailable,
                    _descriptorHeapNativeApiReason,
                    _descriptorHeapProperties,
                    _descriptorHeapNativeFunctions,
                    featurePolicy.RequestedDescriptorBackend);
        }
    }


}
