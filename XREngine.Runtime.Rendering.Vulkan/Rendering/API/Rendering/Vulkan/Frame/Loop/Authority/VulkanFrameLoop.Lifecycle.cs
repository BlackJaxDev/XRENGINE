using Silk.NET.Vulkan;
using System.Runtime.ExceptionServices;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Rendering.DLSS;
using XREngine.Rendering.Vulkan.DeviceBootstrap;

namespace XREngine.Rendering.Vulkan;

/// <summary>Owns native Vulkan startup and shutdown ordering for one frame-loop generation.</summary>
internal sealed partial class VulkanFrameLoop
{
    /// <summary>
    /// Quiesces all producers that can record, submit, or publish native work.
    /// XRWindow invokes this before waiting for GPU idle and destroying wrappers,
    /// so no later submission can invalidate that completion boundary.
    /// </summary>
    internal void BeginBackendRetirement()
    {
        _targetDriver.Quiesce();
        VulkanTextureStreamingBackendProvider.Instance.UnbindScheduler(this);
        QuiesceFrameAdmissionAndWait();
        _commandRuntime.QuiesceCommandChainRecordingWorkersForRetirement();
        _resourceRuntime.Uploads.QuiescePreparationForRetirement(
            "Vulkan renderer retirement before GPU idle",
            TimeSpan.FromSeconds(6));
        _resourceRuntime.PipelineManager.DrainPipelineCompileQueueForShutdown();
        _commandRuntime.CommandBuffers.ReadbackTasks.WaitForPendingTasksOrThrow(TimeSpan.FromSeconds(6));
    }

    internal void Initialize()
    {
        if (_initializationStage is not VulkanFrameLoopInitializationStage.None)
        {
            throw new InvalidOperationException(
                $"The Vulkan frame loop cannot initialize from lifecycle stage '{_initializationStage}'.");
        }

        try
        {
            VulkanIndirectCommandLayoutContract.ValidateRuntimeLayout();

            if (_targetDriver.SupportsStreamlinePresentation)
                _outputRuntime.PrepareStreamlineVulkanRequirements(
                    isSecondaryGpuContext: false,
                    _telemetry._diagnosticOptions.RenderDocFriendly);

            EnterInitializationStage(VulkanFrameLoopInitializationStage.Instance);
            InitializeDeviceBootstrap();
            EnterInitializationStage(VulkanFrameLoopInitializationStage.TargetInstanceResources);
            CreateTargetInstanceResources(Api, _window);
            EnterInitializationStage(VulkanFrameLoopInitializationStage.OutputServices);
            AttachOutputServices(Api);
            EnterInitializationStage(VulkanFrameLoopInitializationStage.PhysicalDevice);
            SelectPhysicalDevice();
            if (_targetDriver.SupportsStreamlinePresentation)
                _outputRuntime.ValidateStreamlineSelectedPhysicalDevice((nint)_deviceContext.PhysicalDevice.Handle);

            EnterInitializationStage(VulkanFrameLoopInitializationStage.LogicalDevice);
            CreateLogicalDevice();
            EnterInitializationStage(VulkanFrameLoopInitializationStage.MemoryAllocator);
            _resourceRuntime.InitializeMemoryAllocator(
                Api,
                _deviceContext,
                RuntimeEngine.Rendering.Settings.VulkanRobustnessSettings.AllocatorBackend,
                _deviceContext.SupportsBufferDeviceAddress);
            _resourceRuntime.Descriptors.FinalizeLogicalDevicePublication();
            EnterInitializationStage(VulkanFrameLoopInitializationStage.StreamingScheduler);
            VulkanTextureStreamingBackendProvider.Instance.BindScheduler(this);
            EnterInitializationStage(VulkanFrameLoopInitializationStage.CanonicalSampler);
            VulkanCanonicalImmutableSamplerService.Initialize(_resourceRuntime, Api, _deviceContext);
            EnterInitializationStage(VulkanFrameLoopInitializationStage.CommandPool);
            _commandRuntime.CreateCommandPool();

            EnterInitializationStage(VulkanFrameLoopInitializationStage.RootDescriptorLayout);
            VulkanRootDescriptorLayoutService.Create(_resourceRuntime, Api, _deviceContext.Device);
            EnterInitializationStage(VulkanFrameLoopInitializationStage.TargetFinalOutput);
            InitializeTargetFinalOutput();
            if (_targetDriver is VulkanDesktopWsiTargetDriver)
            {
                EnterInitializationStage(VulkanFrameLoopInitializationStage.DesktopSwapchain);
                CreateInitialDesktopSwapchainGeneration();
            }

            EnterInitializationStage(VulkanFrameLoopInitializationStage.SynchronizationObjects);
            CreateSyncObjects();
            EnterInitializationStage(VulkanFrameLoopInitializationStage.FrameTiming);
            CreateFrameTimingResources();
            EnterInitializationStage(VulkanFrameLoopInitializationStage.SynchronizationBackend);
            _commandRuntime.InitializeSynchronizationBackend(_deviceContext.SupportsSynchronization2);
            EnterInitializationStage(VulkanFrameLoopInitializationStage.MappedFrameArena);
            _resourceRuntime.InitializeMappedFrameArena(_deviceContext, FrameSlotCount);
            EnterInitializationStage(VulkanFrameLoopInitializationStage.FrameDataArenas);
            _resourceRuntime.InitializeFrameDataArenas(_deviceContext, FrameSlotCount);
            if (!_resourceRuntime.AdvancedSceneResources.TryInitialize(
                    _deviceContext,
                    out string advancedSceneResourceReason))
            {
                Debug.VulkanWarning(
                    "[VulkanAdvancedScene] Native dual-feed realization is unavailable: {0}",
                    advancedSceneResourceReason);
            }
            ReserveOpenXrFrameDataSlotsIfRequired("initialization");
            int deferredProgramLinkCount = _resourceRuntime.PipelineManager.FlushPendingDeviceReadyProgramLinks();
            if (deferredProgramLinkCount > 0)
            {
                Debug.Vulkan(
                    $"Deferred {deferredProgramLinkCount} Vulkan program link(s) until first use after logical device creation.");
            }
            EnterInitializationStage(VulkanFrameLoopInitializationStage.Initialized);
        }
        catch (Exception initializationFailure)
        {
            try
            {
                CleanUp(waitForGpu: false, gpuIdleAlreadyEstablished: false);
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    "Vulkan initialization failed and reverse-order cleanup also reported failures.",
                    initializationFailure,
                    cleanupFailure);
            }

            ExceptionDispatchInfo.Capture(initializationFailure).Throw();
            throw;
        }
    }

    internal void CleanUp(bool waitForGpu, bool gpuIdleAlreadyEstablished)
    {
        VulkanFrameLoopInitializationStage stage = _initializationStage;
        if (stage is VulkanFrameLoopInitializationStage.None or VulkanFrameLoopInitializationStage.CleanedUp)
            return;
        if (Interlocked.CompareExchange(ref _cleanupInProgress, 1, 0) != 0)
            return;

        QuiesceFrameAdmissionAndWait();
        List<Exception> failures = [];
        bool forceRetirementDrain = false;
        try
        {
            if (stage >= VulkanFrameLoopInitializationStage.TargetInstanceResources)
                RunCleanupStep("target quiesce", _targetDriver.Quiesce, failures);

            bool hasLogicalDevice =
                stage >= VulkanFrameLoopInitializationStage.LogicalDevice &&
                _deviceContext.Device.Handle != 0;
            bool gpuIdleEstablished = hasLogicalDevice && gpuIdleAlreadyEstablished;
            if (hasLogicalDevice && waitForGpu)
                gpuIdleEstablished = RunCleanupStep("GPU completion", WaitForDeviceIdle, failures);

            if (hasLogicalDevice &&
                stage >= VulkanFrameLoopInitializationStage.DesktopSwapchain &&
                _desktopSwapchainService is not null)
            {
                // Normal retirement is marker-driven. Teardown establishes the
                // only device-wide completion boundary and may force the drain.
                RunCleanupStep(
                    "retired desktop target generations",
                    () => DrainRetiredDesktopSwapchainGenerations(force: true),
                    failures);
            }

            // Once device-wide completion is known, recorded pins no longer
            // represent in-flight native use. Keep forced retirement active for
            // the complete reverse-order teardown so every destruction path uses
            // the same completion contract. A non-operational device is likewise
            // no longer capable of advancing marker-driven retirement.
            forceRetirementDrain = hasLogicalDevice &&
                (gpuIdleEstablished || !_deviceContext.StateMachine.IsOperational);
            if (forceRetirementDrain)
                _resourceRuntime.BeginForcedRetirementDrain();

            if (stage >= VulkanFrameLoopInitializationStage.StreamingScheduler)
            {
                RunCleanupStep(
                    "texture streaming scheduler",
                    () => VulkanTextureStreamingBackendProvider.Instance.UnbindScheduler(this),
                    failures);
            }

            if (hasLogicalDevice)
                CleanUpLogicalDeviceResources(stage, failures);

            if (stage >= VulkanFrameLoopInitializationStage.OutputServices)
                RunCleanupStep("output service detachment", DetachOutputServices, failures);
            if (stage >= VulkanFrameLoopInitializationStage.TargetInstanceResources)
            {
                RunCleanupStep(
                    "target instance resources",
                    () => DestroyTargetInstanceResources(Api, _window),
                    failures);
            }
            if (stage >= VulkanFrameLoopInitializationStage.Instance)
            {
                RunCleanupStep(
                    "Vulkan instance",
                    () => _deviceContext.DestroyInstance(
                        Api,
                        _deviceContext.FirstNativeDeviceFault?.Operation),
                    failures);
            }
        }
        finally
        {
            if (forceRetirementDrain)
                _resourceRuntime.EndForcedRetirementDrain();
            _initializationStage = VulkanFrameLoopInitializationStage.CleanedUp;
            Volatile.Write(ref _cleanupInProgress, 0);
        }

        if (failures.Count > 0)
            throw new AggregateException("Vulkan reverse-order teardown reported failures.", failures);
    }

    private void CleanUpLogicalDeviceResources(
        VulkanFrameLoopInitializationStage stage,
        List<Exception> failures)
    {
        const string shutdownReason = "Vulkan renderer shutdown";
        RunCleanupStep("queued texture uploads", () => _resourceRuntime.Uploads.CancelAllQueuedWork(_commandRuntime, shutdownReason), failures);
        RunCleanupStep("imported texture upload frame operations", () => CancelPendingImportedTextureUploadFrameOps(shutdownReason), failures);
        RunCleanupStep("recorded texture upload publications", () => _commandRuntime.CancelRecordedTextureUploadPublications(shutdownReason), failures);
        RunCleanupStep("pipeline compile queue", _resourceRuntime.PipelineManager.DrainPipelineCompileQueueForShutdown, failures);
        RunCleanupStep(
            "accepted frame plans",
            _acceptedFramePlans.ResetAll,
            failures);
        RunCleanupStep(
            "resident draw templates",
            _resourceRuntime.ResidentDrawTemplates.Clear,
            failures);
        RunCleanupStep(
            "resident template frame-slot lifetimes",
            _resourceRuntime.ResidentTemplateFrameSlotLifetimes.ReleaseAll,
            failures);
        RunCleanupStep(
            "queued canonical publication leases",
            MeshOperationRequests.ReleaseCanonicalPublicationLeases,
            failures);
        if (_readbackOutputResourceService is not null)
            RunCleanupStep("post-measurement screenshot readbacks", DrainScreenshotReadbacksForShutdown, failures);
        RunCleanupStep(
            "readback worker tasks",
            () => _commandRuntime.CommandBuffers.ReadbackTasks.WaitForPendingTasks(TimeSpan.FromSeconds(6)),
            failures);
        if (_readbackOutputResourceService is not null)
            RunCleanupStep("screenshot readback resources", DisposeScreenshotReadbacks, failures);
        RunCleanupStep("GPU render statistics readbacks", DisposeGpuRenderStatsReadbacks, failures);
        RunCleanupStep("compute transient resources", _commandRuntime.DestroyComputeTransientResources, failures);
        RunCleanupStep("compute descriptor caches", () => _ = _resourceRuntime.RetireComputeDescriptorCachesForShutdown(), failures);
        RunCleanupStep("dangling Vulkan wrappers", DestroyDanglingWrappers, failures);
        RunCleanupStep(
            "advanced-scene native resources",
            _resourceRuntime.AdvancedSceneResources.RetireAll,
            failures);
        RunCleanupStep("query arenas", _resourceRuntime.Queries.DisposeArenas, failures);
        RunCleanupStep("mesh uniform buffers", _resourceRuntime.DestroyRemainingTrackedMeshUniformBuffers, failures);
        RunCleanupStep("initial retirement drain", ForceFlushAllRetiredResources, failures);

        RunCleanupStep("auto-exposure compute resources", _resourceRuntime.DestroyAutoExposureComputeResources, failures);
        RunCleanupStep("fallback texture", _resourceRuntime.FallbackTexture.RetireAll, failures);
        RunCleanupStep("black fallback texture", _resourceRuntime.BlackFallbackTexture.RetireAll, failures);
        if (_imguiOutputPipelineService is not null &&
            _imguiFontAtlasResources is not null &&
            _imguiDrawBufferResources is not null)
        {
            RunCleanupStep("ImGui resources", DisposeImGuiResources, failures);
        }
        _outputRuntime.RequestImGuiFrameMarkerReset();
        if (_openXrOutputResourceService is not null)
            RunCleanupStep("OpenXR rendering resources", DestroyOpenXrRenderingResources, failures);
        RunCleanupStep("render-graph planner states", DestroyFrameOpResourcePlannerStates, failures);
        RunCleanupStep("auto-exposure history", () => DestroyRetainedAutoExposureHistory("renderer shutdown"), failures);

        VulkanResourceAllocator resourceAllocator = CaptureResourcePlannerRuntimeState().ResourceAllocator;
        RunCleanupStep("render-graph physical images", () => resourceAllocator.DestroyPhysicalImages(BackendObjectContext), failures);
        RunCleanupStep("render-graph physical buffers", () => resourceAllocator.DestroyPhysicalBuffers(BackendObjectContext), failures);
        RunCleanupStep("staging resources", () => _resourceRuntime.Allocations.Staging.Destroy(BackendObjectContext), failures);

        if (stage >= VulkanFrameLoopInitializationStage.FrameDataArenas)
            RunCleanupStep("frame-data arenas", _resourceRuntime.DestroyFrameDataArenas, failures);
        if (stage >= VulkanFrameLoopInitializationStage.MappedFrameArena)
            RunCleanupStep("mapped frame arena", _resourceRuntime.DestroyMappedFrameArena, failures);
        if (stage >= VulkanFrameLoopInitializationStage.SynchronizationBackend)
            _commandRuntime.Synchronization._activeSynchronizationBackend = EVulkanSynchronizationBackend.Legacy;
        if (stage >= VulkanFrameLoopInitializationStage.FrameTiming)
            RunCleanupStep("frame timing resources", DestroyFrameTimingResources, failures);
        if (stage >= VulkanFrameLoopInitializationStage.SynchronizationObjects)
            RunCleanupStep("synchronization objects", DestroySyncObjects, failures);

        if (stage >= VulkanFrameLoopInitializationStage.DesktopSwapchain &&
            _targetDriver is VulkanDesktopWsiTargetDriver &&
            _desktopSwapchainService is not null)
        {
            RunCleanupStep("desktop target generation", DestroyDesktopSwapchainGenerationForShutdown, failures);
        }
        if (stage >= VulkanFrameLoopInitializationStage.TargetFinalOutput &&
            _targetOutputSession is not null)
            RunCleanupStep("target final output", DestroyTargetFinalOutput, failures);

        RunCleanupStep(
            "framebuffer render passes",
            () => _resourceRuntime.Framebuffers.DestroyRenderPasses(Api, _deviceContext.Device),
            failures);
        if (stage >= VulkanFrameLoopInitializationStage.RootDescriptorLayout)
        {
            RunCleanupStep(
                "root descriptor layout",
                () => VulkanRootDescriptorLayoutService.Destroy(
                    _resourceRuntime,
                    Api,
                    _deviceContext.Device,
                    _commandRuntime,
                    CurrentFrameSlot),
                failures);
        }

        RunCleanupStep("late dangling Vulkan wrappers", DestroyDanglingWrappers, failures);
        RunCleanupStep("late mesh uniform buffers", _resourceRuntime.DestroyRemainingTrackedMeshUniformBuffers, failures);
        RunCleanupStep("late retirement drain", ForceFlushAllRetiredResources, failures);
        RunCleanupStep("remaining images", () => _resourceRuntime.Images.DestroyRemaining(Api, _deviceContext.Device), failures);
        RunCleanupStep("tracked pipeline layouts", () => _resourceRuntime.DestroyRemainingTrackedPipelineLayouts(Api, _deviceContext.Device), failures);
        RunCleanupStep("tracked allocations", () => _resourceRuntime.DestroyRemainingTrackedAllocations(BackendObjectContext), failures);

        if (stage >= VulkanFrameLoopInitializationStage.CommandPool)
            RunCleanupStep("command pool", _commandRuntime.DestroyCommandPool, failures);
        RunCleanupStep("final retirement drain", ForceFlushAllRetiredResources, failures);
        RunCleanupStep("final image sweep", () => _resourceRuntime.Images.DestroyRemaining(Api, _deviceContext.Device), failures);
        RunCleanupStep(
            "descriptor set layouts",
            () => _resourceRuntime.DestroyRemainingDescriptorSetLayouts(Api, _deviceContext.Device, CurrentFrameSlot),
            failures);
        RunCleanupStep("shared graphics pipelines", () => _ = _resourceRuntime.PipelineManager.DestroySharedGraphicsPipelines(), failures);
        RunCleanupStep("final tracked pipeline layouts", () => _resourceRuntime.DestroyRemainingTrackedPipelineLayouts(Api, _deviceContext.Device), failures);
        RunCleanupStep("shared graphics pipeline libraries", () => _ = _resourceRuntime.PipelineManager.DestroySharedGraphicsPipelineLibraries(), failures);

        if (stage >= VulkanFrameLoopInitializationStage.MemoryAllocator)
        {
            if (_resourceRuntime.Allocations.Buffers.MemoryAllocator is VulkanBlockAllocator blockAllocator)
                RunCleanupStep("allocator blocks", () => blockAllocator.DestroyAllBlocks(Api, _deviceContext.Device), failures);
            RunCleanupStep("memory allocator", () => _resourceRuntime.Allocations.Buffers.MemoryAllocator?.Dispose(), failures);
            _resourceRuntime.Allocations.Buffers.MemoryAllocator = null;
        }
        if (stage >= VulkanFrameLoopInitializationStage.LogicalDevice)
            RunCleanupStep("logical device", DestroyLogicalDevice, failures);
    }

    private void EnterInitializationStage(VulkanFrameLoopInitializationStage stage)
        => _initializationStage = stage;

    private static bool RunCleanupStep(
        string name,
        Action action,
        List<Exception> failures)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception exception)
        {
            failures.Add(new InvalidOperationException(
                $"Vulkan cleanup step '{name}' failed.",
                exception));
            return false;
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
        bool querySucceeded;
        nint requestedDevice;
        string? failureReason;
        if (_deviceContext.OpenXrBootstrapContext is { } openXrBootstrapContext)
        {
            // XR_KHR_vulkan_enable2 already owns the runtime instance that created this VkInstance.
            // Reuse it for device selection: runtimes such as Monado allow only one live XR instance
            // and correctly reject the old temporary second-instance query with XR_ERROR_LIMIT_REACHED.
            querySucceeded = openXrBootstrapContext.TryGetRequestedVulkanPhysicalDevice(
                (nint)_deviceContext.Instance.Handle,
                out requestedDevice,
                out failureReason);
        }
        else
        {
            querySucceeded = OpenXRAPI.TryGetRequestedVulkanPhysicalDevice(
                (nint)_deviceContext.Instance.Handle,
                out requestedDevice,
                out failureReason);
        }

        if (!querySucceeded)
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
