using Silk.NET.Vulkan;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Rendering.DLSS;
using XREngine.Rendering.Vulkan.DeviceBootstrap;

namespace XREngine.Rendering.Vulkan;

/// <summary>Owns native Vulkan startup and shutdown ordering for one frame-loop generation.</summary>
internal sealed partial class VulkanFrameLoop
{
    internal void Initialize()
    {
        VulkanIndirectCommandLayoutContract.ValidateRuntimeLayout();

        if (_targetDriver.SupportsStreamlinePresentation)
            _outputRuntime.PrepareStreamlineVulkanRequirements(
                isSecondaryGpuContext: false,
                _telemetry._diagnosticOptions.RenderDocFriendly);

        InitializeDeviceBootstrap();
        CreateTargetInstanceResources(Api, _window);
        AttachOutputServices(Api);
        SelectPhysicalDevice();
        if (_targetDriver.SupportsStreamlinePresentation)
            _outputRuntime.ValidateStreamlineSelectedPhysicalDevice((nint)_deviceContext.PhysicalDevice.Handle);

        CreateLogicalDevice();
        _resourceRuntime.InitializeMemoryAllocator(
            Api,
            _deviceContext,
            RuntimeEngine.Rendering.Settings.VulkanRobustnessSettings.AllocatorBackend,
            _deviceContext.SupportsBufferDeviceAddress);
        VulkanTextureStreamingBackendProvider.Instance.BindScheduler(this);
        VulkanCanonicalImmutableSamplerService.Initialize(_resourceRuntime, Api, _deviceContext);
        _commandRuntime.CreateCommandPool();

        VulkanRootDescriptorLayoutService.Create(_resourceRuntime, Api, _deviceContext.Device);
        InitializeTargetFinalOutput();
        if (_targetDriver is VulkanDesktopWsiTargetDriver)
            CreateInitialDesktopSwapchainGeneration();

        CreateSyncObjects();
        CreateFrameTimingResources();
        _commandRuntime.InitializeSynchronizationBackend(_deviceContext.SupportsSynchronization2);
        _resourceRuntime.InitializeMappedFrameArena(_deviceContext, FrameSlotCount);
        _resourceRuntime.InitializeFrameDataArenas(_deviceContext, FrameSlotCount);
        ReserveOpenXrFrameDataSlotsIfRequired("initialization");
        int deferredProgramLinkCount = _resourceRuntime.PipelineManager.FlushPendingDeviceReadyProgramLinks();
        if (deferredProgramLinkCount > 0)
        {
            Debug.Vulkan(
                $"Deferred {deferredProgramLinkCount} Vulkan program link(s) until first use after logical device creation.");
        }
    }

    internal void CleanUp(bool waitForGpu)
    {
        if (_deviceContext.Device.Handle != 0)
        {
            if (waitForGpu)
                WaitForDeviceIdle();

            // Swapchain generations use nonblocking queue-marker fences during normal
            // rendering. The caller establishes the teardown-only GPU-idle boundary.
            DrainRetiredDesktopSwapchainGenerations(force: true);
        }

        bool forceRetirementDrain = !_deviceContext.StateMachine.IsOperational;
        if (forceRetirementDrain)
            _resourceRuntime.BeginForcedRetirementDrain();

        try
        {
            VulkanTextureStreamingBackendProvider.Instance.UnbindScheduler(this);
            _resourceRuntime.Uploads.CancelAllQueuedWork(_commandRuntime, "Vulkan renderer shutdown");
            CancelPendingImportedTextureUploadFrameOps("Vulkan renderer shutdown");
            _commandRuntime.CancelRecordedTextureUploadPublications("Vulkan renderer shutdown");
            _resourceRuntime.PipelineManager.DrainPipelineCompileQueueForShutdown();
            DrainScreenshotReadbacksForShutdown();
            _commandRuntime.CommandBuffers.ReadbackTasks.WaitForPendingTasks(TimeSpan.FromSeconds(6));
            DisposeScreenshotReadbacks();
            DisposeGpuRenderStatsReadbacks();
            _commandRuntime.DestroyComputeTransientResources();
            _resourceRuntime.RetireComputeDescriptorCachesForShutdown();
            DestroyDanglingWrappers();
            _resourceRuntime.Queries.DisposeArenas();
            _resourceRuntime.DestroyRemainingTrackedMeshUniformBuffers();

            // Drain all deferred-deletion queues now that the GPU is idle.
            ForceFlushAllRetiredResources();

            _resourceRuntime.DestroyAutoExposureComputeResources();
            _resourceRuntime.FallbackTexture.RetireAll();
            DisposeImGuiResources();
            _outputRuntime.RequestImGuiFrameMarkerReset();
            DestroyOpenXrRenderingResources();
            DestroyFrameOpResourcePlannerStates();
            if (_targetDriver is VulkanDesktopWsiTargetDriver)
                DestroyDesktopSwapchainGenerationForShutdown();
            DestroyTargetFinalOutput();
            // FBO render passes are NOT destroyed during swapchain recreation
            // (they are swapchain-independent). Clean them up here at full shutdown.
            _resourceRuntime.Framebuffers.DestroyRenderPasses(Api, _deviceContext.Device);
            VulkanRootDescriptorLayoutService.Destroy(
                _resourceRuntime,
                Api,
                _deviceContext.Device,
                _commandRuntime,
                CurrentFrameSlot);
            DestroyRetainedAutoExposureHistory("renderer shutdown");
            VulkanResourceAllocator resourceAllocator = CaptureResourcePlannerRuntimeState().ResourceAllocator;
            resourceAllocator.DestroyPhysicalImages(BackendObjectContext);
            resourceAllocator.DestroyPhysicalBuffers(BackendObjectContext);
            _resourceRuntime.Allocations.Staging.Destroy(BackendObjectContext);
            _resourceRuntime.DestroyFrameDataArenas();
            _resourceRuntime.DestroyMappedFrameArena();

            // Teardown paths above may create or retain late-bound GPU resources.
            // Sweep wrappers and deferred queues before disposing the allocator so
            // final destruction can still free through the correct allocation path.
            DestroyDanglingWrappers();
            _resourceRuntime.DestroyRemainingTrackedMeshUniformBuffers();
            ForceFlushAllRetiredResources();
            _resourceRuntime.Images.DestroyRemaining(Api, _deviceContext.Device);
            _resourceRuntime.DestroyRemainingTrackedPipelineLayouts(Api, _deviceContext.Device);
            _resourceRuntime.DestroyRemainingTrackedAllocations(BackendObjectContext);

            if (_resourceRuntime.Allocations.Buffers.MemoryAllocator is VulkanBlockAllocator blockAllocator)
                blockAllocator.DestroyAllBlocks(Api, _deviceContext.Device);
            _resourceRuntime.Allocations.Buffers.MemoryAllocator?.Dispose();
            _resourceRuntime.Allocations.Buffers.MemoryAllocator = null;
            _commandRuntime.Synchronization._activeSynchronizationBackend = EVulkanSynchronizationBackend.Legacy;
            DestroyFrameTimingResources();

            DestroySyncObjects();
            _commandRuntime.DestroyCommandPool();

            // Flush once more before destroying the logical device to catch any
            // handles retired by sync/command-pool teardown.
            ForceFlushAllRetiredResources();
            _resourceRuntime.Images.DestroyRemaining(Api, _deviceContext.Device);
            _resourceRuntime.DestroyRemainingDescriptorSetLayouts(
                Api,
                _deviceContext.Device,
                CurrentFrameSlot);
            _resourceRuntime.PipelineManager.DestroySharedGraphicsPipelines();
            _resourceRuntime.DestroyRemainingTrackedPipelineLayouts(Api, _deviceContext.Device);
            _resourceRuntime.PipelineManager.DestroySharedGraphicsPipelineLibraries();

            DestroyLogicalDevice();
            DestroyTargetInstanceResources(Api, _window);
            _deviceContext.DestroyInstance(
                Api,
                _deviceContext.FirstNativeDeviceFault?.Operation);
        }
        finally
        {
            if (forceRetirementDrain)
                _resourceRuntime.EndForcedRetirementDrain();
        }
    }

    private void DestroyDanglingWrappers()
        => _resourceRuntime.BackendObjects.DestroyDanglingWrappers();

    private void InitializeDeviceBootstrap()
    {
        _outputRuntime.PrepareObsHookCompatibility();
        OpenXrVulkanRuntimeRequirements openXrRequirements =
            OpenXRAPI.GetRequestedVulkanRuntimeRequirements();
        VulkanDiagnosticOptions diagnostics = _telemetry._diagnosticOptions;
        VulkanDeviceBootstrapResult result = _deviceContext.CreateInstance(
            Api,
            new VulkanDeviceBootstrapRequest(
                _targetDriver.GetRequiredInstanceExtensions(),
                _targetDriver.RequiresSwapchainOutput,
                openXrRequirements.InstanceExtensions,
                openXrRequirements.MinApiVersionSupported,
                openXrRequirements.MaxApiVersionSupported,
                _outputRuntime._streamlineRequiredInstanceExtensions,
                _outputRuntime._streamlineMinimumApiVersion,
                new VulkanDeviceValidationRequest(
                    diagnostics.Preset,
                    diagnostics.Flags,
                    diagnostics.EnableValidationLayers,
                    diagnostics.EnableSynchronizationValidation,
                    diagnostics.EnableGpuAssistedValidation,
                    diagnostics.EnableBestPractices,
                    diagnostics.EnableDebugUtils,
                    diagnostics.EnableCommandBufferLabels,
                    diagnostics.EnableCrashBreadcrumbs,
                    diagnostics.SourceSummary,
                    diagnostics.OverheadWarnings)));
        RuntimeEngine.Rendering.State.VulkanValidationLayersEnabled = result.ValidationLayersEnabled;
        RuntimeEngine.Rendering.State.VulkanSynchronizationValidationEnabled =
            result.SynchronizationValidationEnabled;
    }

    private void SelectPhysicalDevice()
    {
        OpenXrVulkanRuntimeRequirements openXrRequirements =
            OpenXRAPI.GetRequestedVulkanRuntimeRequirements();
        VulkanOpenXrRequestedDeviceFacts openXrRequestedDevice = ResolveOpenXrRequestedDevice();
        VulkanDeviceExtensionRequirements extensions = new(
            _targetDriver.RequiredDeviceExtensions,
            _outputRuntime._streamlineRequiredDeviceExtensions,
            openXrRequirements.DeviceExtensions);
        VulkanOutputDeviceRequirements outputRequirements = new(
            _targetDriver.RequiresPresentQueue,
            _targetDriver.RequiresSwapchainOutput);

        foreach (PhysicalDevice physicalDevice in _deviceContext.EnumeratePhysicalDevices(Api))
        {
            VulkanPhysicalDeviceCapabilitySnapshot capabilities =
                VulkanDeviceCapabilityQuery.Query(Api, physicalDevice);
            VulkanOutputDeviceProbeFacts outputProbe =
                _outputRuntime.QueryPhysicalDeviceSelectionFacts(
                    physicalDevice,
                    checked((uint)capabilities.QueueFamilyArray.Length));
            if (!_deviceContext.TrySelectPhysicalDevice(
                    new VulkanPhysicalDeviceSelectionRequest(
                        physicalDevice,
                        capabilities,
                        outputRequirements,
                        outputProbe,
                        extensions,
                        openXrRequestedDevice),
                    out VulkanPhysicalDeviceSelectionResult selection))
            {
                continue;
            }

            PublishSelectedDeviceState(selection);
            return;
        }

        if (openXrRequestedDevice.HasRequestedDevice)
        {
            throw new InvalidOperationException(
                $"The OpenXR runtime-selected Vulkan physical device 0x{(nuint)openXrRequestedDevice.RequestedDeviceHandle:X} is not suitable for this Vulkan target.");
        }

        throw new InvalidOperationException("Failed to find a suitable GPU for Vulkan.");
    }

    private VulkanOpenXrRequestedDeviceFacts ResolveOpenXrRequestedDevice()
    {
        if (!OpenXRAPI.TryGetRequestedVulkanPhysicalDevice(
                (nint)_deviceContext.Instance.Handle,
                out nint requestedDevice,
                out string? failureReason))
        {
            if (!string.IsNullOrWhiteSpace(failureReason))
            {
                throw new InvalidOperationException(
                    $"Failed to query the OpenXR runtime-selected Vulkan physical device: {failureReason}");
            }

            return VulkanOpenXrRequestedDeviceFacts.None;
        }

        return new VulkanOpenXrRequestedDeviceFacts(true, requestedDevice);
    }

    private static unsafe void PublishSelectedDeviceState(in VulkanPhysicalDeviceSelectionResult selection)
    {
        PhysicalDeviceProperties properties = selection.Capabilities.Properties;
        RuntimeEngine.Rendering.State.IsNVIDIA = properties.VendorID == 0x10DE;
        RuntimeEngine.Rendering.State.IsIntel = properties.VendorID == 0x8086;
        RuntimeEngine.Rendering.State.IsVulkan = true;
        RuntimeEngine.Rendering.State.SupportsOpenGLLayeredFramebuffers = false;
        RuntimeEngine.Rendering.State.SupportsOpenGLGeometryShaderLayeredRendering = false;
        RuntimeEngine.Rendering.State.SupportsOpenGLVertexShaderLayeredRendering = false;
        RuntimeEngine.Rendering.State.SupportsOpenGLViewportArray = false;
        RuntimeEngine.Rendering.State.SupportsOpenGLViewportScissorArray = false;
        RuntimeEngine.Rendering.State.SupportsOpenGLVertexShaderViewportIndex = false;
        RuntimeEngine.Rendering.State.SupportsOpenGLGeometryShaderViewportIndex = false;
        RuntimeEngine.Rendering.State.MaxOpenGLViewports = 1;
        RuntimeEngine.Rendering.State.VulkanDeviceName = Silk.NET.Core.Native.SilkMarshal.PtrToString((nint)properties.DeviceName);
        RuntimeEngine.Rendering.State.VulkanVendorId = properties.VendorID;
        RuntimeEngine.Rendering.State.VulkanDeviceId = properties.DeviceID;
        RuntimeEngine.Rendering.State.HasVulkanRayTracing = selection.SupportsRayTracing;
    }

    private void CreateLogicalDevice()
    {
        VulkanStreamlineProvisioningSnapshot provisioning = _outputRuntime.CaptureStreamlineProvisioning();
        VulkanStreamlineProvisioningSnapshot withoutFrameGeneration =
            provisioning.FrameGenerationProvisioned
                ? VulkanUpscaleBridgeSidecar.ResolveStreamlineVulkanRequirements(
                    provisioning.DlssProvisioned,
                    includeFrameGeneration: false)
                : provisioning;
        static VulkanLogicalDeviceBootstrapRequest.StreamlineRequirementSet ToRequirementSet(
            VulkanStreamlineProvisioningSnapshot snapshot)
            => new(
                snapshot.DlssProvisioned,
                snapshot.FrameGenerationProvisioned,
                snapshot.RequiredDeviceExtensions,
                snapshot.RequiredFeatures12,
                snapshot.RequiredFeatures13,
                snapshot.QueueRequirements);
        VulkanLogicalDeviceBootstrapRequest.StreamlineRequirements streamline = new(
            ToRequirementSet(provisioning),
            ToRequirementSet(withoutFrameGeneration),
            VulkanLogicalDeviceBootstrapRequest.StreamlineRequirementSet.Empty,
            RuntimeEngine.EffectiveSettings.EnableNvidiaDlss ||
                RuntimeEngine.EffectiveSettings.AntiAliasingMode == EAntiAliasingMode.Dlaa,
            NvidiaDlssManager.IsFrameGenerationRequested);
        VulkanLogicalDeviceBootstrapRequest.FeaturePolicyFacts featurePolicy = new(
            VulkanFeatureProfile.RequestedCapabilityTier,
            VulkanFeatureProfile.RequestedDescriptorBackend,
            VulkanFeatureProfile.RequestedProgramBindingBackend,
            VulkanFeatureProfile.RequestedFoveationBackend,
            VulkanFeatureProfile.RequestedRayTracingBackend,
            VulkanFeatureProfile.ActiveGeometryFetchMode,
            VulkanFeatureProfile.EnableDescriptorIndexing,
            VulkanFeatureProfile.EnableBindlessMaterialTable,
            VulkanFeatureProfile.RequireBindlessMaterialTable,
            VulkanFeatureProfile.EnableRtxIoVulkanDecompression,
            VulkanFeatureProfile.EnableRtxIoVulkanCopyMemoryIndirect,
            VulkanFeatureProfile.TryGetCapabilityTierEnvOverride(out EVulkanCapabilityTier capabilityTier) ? capabilityTier : null,
            VulkanFeatureProfile.TryGetDescriptorBackendEnvOverride(out EVulkanDescriptorBackend descriptorBackend) ? descriptorBackend : null,
            VulkanFeatureProfile.TryGetProgramBindingBackendEnvOverride(out EVulkanProgramBindingBackend programBindingBackend) ? programBindingBackend : null,
            VulkanFeatureProfile.TryGetFoveationBackendEnvOverride(out EVulkanFoveationBackend foveationBackend) ? foveationBackend : null,
            VulkanFeatureProfile.TryGetRayTracingBackendEnvOverride(out EVulkanRayTracingBackend rayTracingBackend) ? rayTracingBackend : null);
        VulkanDeviceExtensionRequirements extensions = new(
            _targetDriver.RequiredDeviceExtensions,
            provisioning.RequiredDeviceExtensions,
            OpenXRAPI.GetRequestedVulkanRuntimeRequirements().DeviceExtensions);
        VulkanLogicalDeviceBootstrapResult result = _deviceContext.BootstrapLogicalDevice(
            new VulkanLogicalDeviceBootstrapRequest(
                extensions,
                new VulkanLogicalDeviceBootstrapRequest.OutputRequirements(
                    _targetDriver.RequiresPresentQueue,
                    _targetDriver.RequiresSwapchainOutput,
                    VulkanOutputRuntime.ResolveRequestedRenderTargetMode(),
                    _targetDriver.RequiresSwapchainOutput,
                    _outputRuntime.ObsHook.LayerAvailable,
                    _outputRuntime.ObsHook.Policy == EVulkanObsHookPolicy.Require),
                streamline,
                featurePolicy,
                _telemetry._diagnosticOptions,
                new VulkanLogicalDeviceBootstrapRequest.LayeredShadowPolicy(true)));
        ApplyLogicalDevicePublication(result);
    }

    private void ApplyLogicalDevicePublication(VulkanLogicalDeviceBootstrapResult result)
    {
        _outputRuntime._streamlineDlssProvisioned = result.Output.StreamlineDlssProvisioned;
        _outputRuntime._streamlineFrameGenerationProvisioned = result.Output.StreamlineFrameGenerationProvisioned;
        _outputRuntime._streamlineGraphicsQueueFamily = result.Output.StreamlineGraphicsQueueFamily;
        _outputRuntime._streamlineGraphicsQueueIndex = result.Output.StreamlineGraphicsQueueIndex;
        _outputRuntime._streamlineComputeQueueFamily = result.Output.StreamlineComputeQueueFamily;
        _outputRuntime._streamlineComputeQueueIndex = result.Output.StreamlineComputeQueueIndex;
        _outputRuntime._streamlineOpticalFlowQueueFamily = result.Output.StreamlineOpticalFlowQueueFamily;
        _outputRuntime._streamlineOpticalFlowQueueIndex = result.Output.StreamlineOpticalFlowQueueIndex;
        _outputRuntime._streamlineRequiredDeviceExtensions = result.Output.StreamlineRequiredDeviceExtensions;
        _outputRuntime._streamlineRequiredFeatures12 = result.Output.StreamlineRequiredFeatures12;
        _outputRuntime._streamlineRequiredFeatures13 = result.Output.StreamlineRequiredFeatures13;
        _outputRuntime._streamlineQueueRequirements = result.Output.StreamlineQueueRequirements;
        _outputRuntime._requestedRenderTargetMode = result.Output.RequestedRenderTargetMode;
        _outputRuntime.Desktop.Maintenance1Enabled = result.Output.SwapchainMaintenance1Enabled;

        VulkanLogicalDeviceBootstrapResult.QueryPublication queries = result.Resources.Queries;
        _resourceRuntime.Queries.OcclusionPreciseAdvertised = queries.OcclusionPreciseAdvertised;
        _resourceRuntime.Queries.OcclusionPreciseEnabled = queries.OcclusionPreciseEnabled;
        _resourceRuntime.Queries.PipelineStatisticsAdvertised = queries.PipelineStatisticsAdvertised;
        _resourceRuntime.Queries.PipelineStatisticsEnabled = queries.PipelineStatisticsEnabled;
        _resourceRuntime.Queries.InheritedQueriesAdvertised = queries.InheritedQueriesAdvertised;
        _resourceRuntime.Queries.InheritedQueriesEnabled = queries.InheritedQueriesEnabled;
        _resourceRuntime.Queries.MeshShaderQueriesEnabled = queries.MeshShaderQueriesEnabled;
        _resourceRuntime.Queries.HostResetAdvertised = queries.HostResetAdvertised;
        _resourceRuntime.Queries.PrimitivesGeneratedAdvertised = queries.PrimitivesGeneratedAdvertised;
        _resourceRuntime.Queries.PrimitivesGeneratedEnabled = queries.PrimitivesGeneratedEnabled;
        _resourceRuntime.Queries.PrimitivesGeneratedNonZeroStreamsEnabled = queries.PrimitivesGeneratedNonZeroStreamsEnabled;
        _resourceRuntime.Descriptors.ApplyLogicalDevicePublication(result.Resources.Descriptors);
        _commandRuntime.ApplyLogicalDevicePublication(result.Commands);
        RuntimeEngine.Rendering.State.HasVulkanMultiView = result.Engine.HasVulkanMultiView;
        RuntimeEngine.Rendering.State.HasVulkanDepthClipControl = result.Engine.HasVulkanDepthClipControl;
        RuntimeEngine.Rendering.State.HasVulkanMemoryDecompression = result.Engine.HasVulkanMemoryDecompression;
        RuntimeEngine.Rendering.State.HasVulkanCopyMemoryIndirect = result.Engine.HasVulkanCopyMemoryIndirect;
        VulkanDeviceCapabilityReporter.ReportLayeredShadowCapabilities(_deviceContext, result.LayeredShadows);
    }

    private void DestroyLogicalDevice()
    {
        _resourceRuntime.PipelineManager.ClearPendingDeviceReadyProgramLinks();
        if (!_deviceContext.HasLogicalDevice)
            return;

        _resourceRuntime.Descriptors.DestroyGlobalMaterialTextureDescriptorTable();
        _resourceRuntime.Descriptors.DestroyDescriptorHeapBackend();
        _resourceRuntime.DescriptorLifetime.DestroyUpdateTemplateCache();
        _resourceRuntime.Descriptors.DestroyCachedDescriptorSetLayouts();
        _resourceRuntime.PipelineManager.DestroyPipelineCache();
        VulkanCanonicalImmutableSamplerService.Destroy(_resourceRuntime, Api, _deviceContext.Device);
        _resourceRuntime.Samplers.DestroyRemaining(Api, _deviceContext.Device);
        _deviceContext.Destroy(Api);
    }
}
