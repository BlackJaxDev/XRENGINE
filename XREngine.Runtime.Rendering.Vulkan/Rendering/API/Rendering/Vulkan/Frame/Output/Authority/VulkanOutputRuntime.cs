using System.Threading;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using XREngine.Rendering.DLSS;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Rendering.Vulkan.RenderGraph;
using XREngine.Rendering.UI;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns the renderer-wide output identities which are independent of command
/// planning: the selected target policy, OpenXR output state, and the explicit
/// target-frame sequence.
/// </summary>
internal sealed class VulkanOutputRuntime
{
    internal static readonly object Phase524bDesktopRejectionEvidenceLock = new();
    internal static OpenXrSmokeDesktopRejectionEvidence Phase524bDesktopRejectionEvidence = new();
    internal VulkanDesktopAcquireAvailabilityTracker _desktopAcquireAvailability;
    internal VulkanDesktopFrameFaultInjectionState _desktopFrameFaultInjection = new();
    internal VulkanDesktopSwapchainPolicyState _desktopSwapchainPolicy = new();
    internal int _hasPresentedCompleteSceneFrame;
    internal VulkanImGuiBackend? _imguiBackend;
    private VulkanImGuiFontAtlasResources? _imguiFontAtlasResources;
    private VulkanImGuiDrawBufferResources? _imguiDrawBufferResources;
    private VulkanImGuiOutputPipelineService? _imguiOutputPipelineService;
    private VulkanImGuiOverlayAdmission? _imguiOverlayAdmission;
    private VulkanImGuiTextureOutputResources? _imguiTextureOutputResources;
    private VulkanImGuiTextureRegistryService? _imguiTextureRegistryService;
    internal VulkanImGuiDrawDataCache _imguiDrawData = new();
    internal VulkanImGuiResources _imguiResources = new();
    internal VulkanImGuiTextureRegistry _imguiTextureRegistry = new();
    internal readonly List<Fence> _orphanedSwapchainMarkerFences = new(2);
    internal Phase524bDesktopRejectionInjection _phase524bDesktopRejectionInjection = new();
    internal Phase524bDesktopRejectionDecision _phase524bPendingDesktopRejection;
    internal EVulkanRenderTargetMode _requestedRenderTargetMode = EVulkanRenderTargetMode.Auto;
    internal readonly List<RetiredSwapchainGeneration> _retiredSwapchainGenerations = new(8);
    internal uint _streamlineComputeQueueFamily;
    internal uint _streamlineComputeQueueIndex;
    internal bool _streamlineDlssProvisioned;
    internal bool _streamlineFrameGenerationProvisioned;
    internal uint _streamlineGraphicsQueueFamily;
    internal uint _streamlineGraphicsQueueIndex;
    internal uint _streamlineMinimumApiVersion = Vk.Version11;
    internal uint _streamlineOpticalFlowQueueFamily;
    internal uint _streamlineOpticalFlowQueueIndex;
    internal NvidiaDlssManager.Native.StreamlineQueueRequirements _streamlineQueueRequirements;
    internal string[] _streamlineRequiredDeviceExtensions = [];
    internal string[] _streamlineRequiredFeatures12 = [];
    internal string[] _streamlineRequiredFeatures13 = [];
    internal string[] _streamlineRequiredInstanceExtensions = [];
    internal KhrSurface? SurfaceApi;
    internal SurfaceKHR Surface;
    private VulkanTargetOutputContext? _targetOutputContext;
    private VulkanDesktopSwapchainService? _desktopSwapchainService;
    private VulkanOpenXrOutputResourceService? _openXrOutputResourceService;
    private VulkanReadbackOutputResourceService? _readbackOutputResourceService;
    private long _explicitTargetFrameNumber;
    private int _imguiFrameMarkerResetRequested;

    internal VulkanOutputRuntime(IVulkanRendererTargetDriver targetDriver)
    {
        TargetDriver = targetDriver;
        OpenXrBackend = new VulkanOpenXrBackend();
        ImGuiPlatformWindows = new VulkanImGuiPlatformWindowOutputAuthority();
        Desktop = new VulkanDesktopOutputState();
        Capture = new VulkanCaptureOutputState(
            screenshotReadbackSlotCount: 8,
            gpuStatsReadbackSlotCount: 32);
        ObsHook = new VulkanObsHookOutputState();
        StreamlineUi = new VulkanStreamlineUiOutputState();
        PresentationSource = new VulkanPresentationSourceState();
    }

    internal IVulkanRendererTargetDriver TargetDriver { get; }
    internal VulkanOpenXrBackend OpenXrBackend { get; }
    internal VulkanImGuiPlatformWindowOutputAuthority ImGuiPlatformWindows { get; }
    internal VulkanDesktopOutputState Desktop { get; }
    internal VulkanCaptureOutputState Capture { get; }
    internal VulkanObsHookOutputState ObsHook { get; }
    internal VulkanStreamlineUiOutputState StreamlineUi { get; }
    internal VulkanPresentationSourceState PresentationSource { get; }
    internal VulkanImGuiOverlayAdmission GetImGuiOverlayAdmission(
        VulkanResourceRuntime resourceRuntime,
        VulkanDeviceContext deviceContext)
        => _imguiOverlayAdmission ??= new VulkanImGuiOverlayAdmission(this, resourceRuntime, deviceContext);
    internal VulkanImGuiFontAtlasResources GetImGuiFontAtlasResources(
        VulkanResourceRuntime resourceRuntime,
        VulkanCommandRuntime commandRuntime,
        VulkanDeviceContext deviceContext)
        => _imguiFontAtlasResources ??= new VulkanImGuiFontAtlasResources(
            this,
            resourceRuntime,
            commandRuntime,
            deviceContext);
    internal VulkanImGuiDrawBufferResources GetImGuiDrawBufferResources(
        VulkanResourceRuntime resourceRuntime)
        => _imguiDrawBufferResources ??= new VulkanImGuiDrawBufferResources(
            this,
            resourceRuntime);
    internal VulkanImGuiOutputPipelineService GetImGuiOutputPipelineService(
        VulkanResourceRuntime resourceRuntime,
        VulkanDeviceContext deviceContext)
        => _imguiOutputPipelineService ??= new VulkanImGuiOutputPipelineService(
            this,
            resourceRuntime,
            deviceContext);
    internal VulkanImGuiTextureOutputResources GetImGuiTextureOutputResources(
        VulkanResourceRuntime resourceRuntime,
        VulkanDeviceContext deviceContext)
        => _imguiTextureOutputResources ??= new VulkanImGuiTextureOutputResources(
            deviceContext,
            resourceRuntime);
    internal VulkanImGuiTextureRegistryService GetImGuiTextureRegistryService(
        VulkanResourceRuntime resources,
        VulkanCommandRuntime commands,
        VulkanDeviceContext device)
        => _imguiTextureRegistryService ??= new VulkanImGuiTextureRegistryService(
            this,
            resources,
            commands,
            device);

    internal void StoreImGuiDrawData(ImGuiNET.ImDrawDataPtr drawData)
        => _imguiDrawData.Store(drawData);

    internal VulkanImGuiBackend GetOrCreateImGuiBackend(VulkanImGuiServices services)
    {
        VulkanImGuiBackend? backend = _imguiBackend;
        if (backend is not null && !ImGuiContextTracker.IsAlive(backend.ContextHandle))
        {
            backend.Dispose();
            _imguiBackend = null;
            _imguiDrawData.Clear();
        }

        return _imguiBackend ??= new VulkanImGuiBackend(services);
    }

    internal void DisposeImGuiResources(
        VulkanResourceRuntime resources,
        VulkanCommandRuntime commands,
        VulkanDeviceContext device)
    {
        _imguiBackend?.Dispose();
        _imguiBackend = null;

        GetImGuiOutputPipelineService(resources, device).Dispose();
        GetImGuiFontAtlasResources(resources, commands, device).RetireAll();
        GetImGuiDrawBufferResources(resources).RetireAll();
        _imguiDrawData.Clear();
    }
    internal VulkanDesktopSwapchainService DesktopSwapchainService
        => _desktopSwapchainService ?? throw new InvalidOperationException("The desktop swapchain service is not initialized.");
    internal VulkanOpenXrOutputResourceService GetOpenXrOutputResourceService(
        Vk api,
        VulkanDeviceContext deviceContext,
        VulkanCommandRuntime commandRuntime,
        VulkanResourceRuntime resourceRuntime,
        VulkanFrameTelemetry telemetry)
        => _openXrOutputResourceService ??= new VulkanOpenXrOutputResourceService(
            this,
            api,
            deviceContext,
            commandRuntime,
            resourceRuntime,
            telemetry);
    internal VulkanReadbackOutputResourceService GetReadbackOutputResourceService(
        VulkanDeviceContext deviceContext,
        VulkanResourceRuntime resourceRuntime,
        VulkanCommandRuntime commandRuntime)
        => _readbackOutputResourceService ??= new VulkanReadbackOutputResourceService(
            deviceContext,
            resourceRuntime,
            commandRuntime);
    internal VulkanTargetOutputContext TargetOutputContext
        => _targetOutputContext ?? throw new InvalidOperationException(
            "The Vulkan target output context is not initialized.");

    /// <summary>
    /// Returns the atomically published desktop depth target.  Frame recording
    /// reads this through the output authority so it never needs a renderer
    /// field mirror while a WSI generation is being replaced.
    /// </summary>
    internal VulkanSwapchainDepthResources? DesktopDepthResources
        => Volatile.Read(ref Desktop.DepthResources);

    internal Image DesktopDepthImage => DesktopDepthResources?.Image ?? default;
    internal ImageView DesktopDepthView => DesktopDepthResources?.View ?? default;
    internal Format DesktopDepthFormat => DesktopDepthResources?.Format ?? default;
    internal ImageAspectFlags DesktopDepthAspect => DesktopDepthResources?.Aspect ?? default;

    internal void InitializeDesktopSwapchainService(
        Vk api,
        VulkanDeviceContext deviceContext,
        VulkanCommandRuntime commandRuntime,
        VulkanResourceRuntime resourceRuntime,
        VulkanFrameTelemetry telemetry,
        VulkanFramePlanner framePlanner)
        => _desktopSwapchainService ??= new VulkanDesktopSwapchainService(
            this,
            api,
            deviceContext,
            commandRuntime,
            resourceRuntime,
            telemetry,
            framePlanner);

    /// <summary>
    /// Provides the frame-loop and bootstrap authorities with renderer-free desktop
    /// WSI lifecycle entry points.  The swapchain service owns native WSI and
    /// Streamline proxy calls; callers only coordinate their local resources.
    /// </summary>
    internal void CreateDesktopWsiGeneration(SwapchainKHR oldSwapchain = default)
        => DesktopSwapchainService.CreateSwapchain(oldSwapchain);

    internal void CreateInitialDesktopSwapchainGeneration()
        => DesktopSwapchainService.CreateInitialGeneration();

    internal void DestroyDesktopWsiGeneration()
        => DesktopSwapchainService.DestroySwapchain();

    internal void DestroyDesktopSwapchainGenerationForShutdown()
        => DesktopSwapchainService.DestroyLiveGenerationForShutdown();

    internal void CreateDesktopPresentBridgeSemaphores(int imageCount)
        => DesktopSwapchainService.CreateLivePresentBridgeSemaphores(imageCount);

    internal void DestroyDesktopPresentBridgeSemaphores()
        => DesktopSwapchainService.DestroyLivePresentBridgeSemaphores();

    internal void CreateDesktopSwapchainImageViews()
        => DesktopSwapchainService.CreateImageViews();

    internal void DestroyDesktopSwapchainImageViews()
        => DesktopSwapchainService.DestroyImageViews();

    internal void CreateDesktopDepthResources()
        => DesktopSwapchainService.CreateDepthResources();

    internal VulkanSwapchainDepthResources? DetachDesktopDepthResources()
        => DesktopSwapchainService.DetachDepthResources();

    internal bool IsDesktopSurfacePresentable(out string reason)
        => DesktopSwapchainService.IsSurfacePresentable(out reason);

    internal SwapChainSupportDetails QueryDesktopSwapchainSupport(PhysicalDevice physicalDevice)
        => DesktopSwapchainService.QuerySupport(physicalDevice);

    internal void DisableDesktopStreamlineFrameGenerationForMutation(string reason)
        => DesktopSwapchainService.DisableStreamlineFrameGenerationBeforeMutation(reason);

    internal void DrainDesktopStreamlineFrameGenerationDisableBeforePresent()
        => DesktopSwapchainService.DrainStreamlineFrameGenerationDisableBeforePresent();

    internal void DrainRetiredDesktopSwapchainGenerations(bool force = false)
        => DesktopSwapchainService.DrainRetiredGenerations(force);

    internal bool TryPrepareDesktopSwapchainRetirementMarkers(out Fence graphicsMarkerFence, out Fence presentMarkerFence)
        => DesktopSwapchainService.TryPrepareRetirementMarkers(out graphicsMarkerFence, out presentMarkerFence);

    internal void QueueRetiredDesktopSwapchainGeneration(RetiredSwapchainGeneration generation)
        => DesktopSwapchainService.QueueRetiredGeneration(generation);

    /// <summary>
    /// Recreates the native desktop WSI generation through the output authority.
    /// The frame loop deliberately has no renderer facade dependency for this
    /// operation; all device, resource, command, and telemetry authorities were
    /// captured when the desktop swapchain service was initialized.
    /// </summary>
    internal bool TryRecreateDesktopSwapchain()
        => DesktopSwapchainService.TryRecreateGeneration();

    internal void RequestImGuiFrameMarkerReset()
        => Interlocked.Exchange(ref _imguiFrameMarkerResetRequested, 1);

    internal bool ConsumeImGuiFrameMarkerResetRequest()
        => Interlocked.Exchange(ref _imguiFrameMarkerResetRequested, 0) != 0;

    internal void RecordPhase524bInjectedDesktopRejection(
        in FrameOpContext context,
        in RejectedDesktopFramePolicyDecision policy,
        bool presentAccepted,
        ulong renderFrameId)
    {
        Phase524bDesktopRejectionDecision sample =
            _phase524bPendingDesktopRejection;
        OpenXrSmokeDesktopRejectionEvidence evidence = new()
        {
            Injected = true,
            Observed = true,
            Policy = policy.Disposition.ToString(),
            SkippedPresent = !policy.ShouldPresent,
            PresentedLastCompletedImage = policy.ShouldPresent,
            PresentAccepted = presentAccepted,
            ClearedTargetPublished = false,
            PipelineName = context.PipelineInstance?.DebugName ?? "<unknown>",
            PipelineInstanceId = context.PipelineIdentity,
            OutputId = unchecked((ulong)(uint)context.ViewportIdentity),
            RenderFrameId = renderFrameId,
            ManifestFrameId = 0UL,
            Exposure = sample.Exposure,
            ExposureHistory = sample.ExposureHistory,
            ExposureFinite = double.IsFinite(sample.Exposure),
            ExposureHistoryFinite = double.IsFinite(sample.ExposureHistory),
            ExposureNonZeroRequired = true,
            ExposureHistoryNonZeroRequired = true,
            ExposureOwnerMatchesDesktop =
                context.ContextKind == EVulkanFrameOpContextKind.MainViewport &&
                context.PipelineIdentity != 0 &&
                context.ResourceRegistry?.TextureRecords.ContainsKey(
                    DefaultRenderPipeline.AutoExposureTextureName) == true,
            Diagnostic = sample.Diagnostic,
        };

        lock (Phase524bDesktopRejectionEvidenceLock)
            Phase524bDesktopRejectionEvidence = evidence;
    }

    internal VulkanStreamlineDeviceBinding CaptureStreamlineDeviceBinding(
        VulkanDeviceContext deviceContext)
        => new(
            deviceContext.Device,
            deviceContext.Instance,
            deviceContext.PhysicalDevice,
            _streamlineComputeQueueIndex,
            _streamlineComputeQueueFamily,
            _streamlineGraphicsQueueIndex,
            _streamlineGraphicsQueueFamily,
            _streamlineOpticalFlowQueueIndex,
            _streamlineOpticalFlowQueueFamily,
            _streamlineQueueRequirements.OpticalFlowQueues > 0,
            _streamlineDlssProvisioned,
            _streamlineFrameGenerationProvisioned,
            Desktop.StreamlineFrameGenerationIncludesDlss);

    /// <summary>Creates target-owned instance resources through the narrow WSI surface authority.</summary>
    internal void CreateTargetInstanceResources(Vk api, VulkanDeviceContext deviceContext, Silk.NET.Windowing.IWindow? window)
        => TargetDriver.CreateInstanceResources(new VulkanTargetSurfaceAuthority(api, deviceContext, this, window));

    /// <summary>Initializes the selected target's final output with its scoped native access.</summary>
    internal void InitializeTargetFinalOutput(
        Vk api,
        VulkanDeviceContext deviceContext,
        VulkanCommandRuntime commandRuntime,
        VulkanResourceRuntime resourceRuntime,
        VulkanFrameTelemetry telemetry)
    {
        if (_targetOutputContext is not null)
            throw new InvalidOperationException("The Vulkan target output context is already initialized.");

        VulkanTargetOutputContext context = new(
            api,
            deviceContext,
            commandRuntime,
            resourceRuntime,
            telemetry,
            this);
        _targetOutputContext = context;
        try
        {
            TargetDriver.InitializeFinalOutput(context);
        }
        catch
        {
            _targetOutputContext = null;
            throw;
        }
    }

    /// <summary>Destroys target-owned final output before device teardown.</summary>
    internal void DestroyTargetFinalOutput()
    {
        if (_targetOutputContext is null)
            return;

        try
        {
            TargetDriver.DestroyFinalOutput(_targetOutputContext);
        }
        finally
        {
            _targetOutputContext = null;
        }
    }

    /// <summary>Destroys target-owned instance resources after device teardown.</summary>
    internal void DestroyTargetInstanceResources(Vk api, VulkanDeviceContext deviceContext, Silk.NET.Windowing.IWindow? window)
        => TargetDriver.DestroyInstanceResources(new VulkanTargetSurfaceAuthority(api, deviceContext, this, window));

    internal long NextExplicitTargetFrameNumber()
        => Interlocked.Increment(ref _explicitTargetFrameNumber);

    internal VulkanOutputRuntimeSnapshot CaptureSnapshot()
        => new(
            TargetDriver.ExecutionMode,
            TargetDriver.GetType().Name,
            TargetDriver is IVulkanExplicitFrameTargetDriver,
            Volatile.Read(ref _explicitTargetFrameNumber));

    internal VulkanDesktopWsiTargetDriver RequireDesktopWsiTarget()
        => TargetDriver as VulkanDesktopWsiTargetDriver
            ?? throw new InvalidOperationException(
                $"Vulkan target '{TargetDriver.ExecutionMode}' does not provide desktop WSI policy.");

    internal IVulkanExplicitFrameTargetDriver RequireExplicitFrameTarget()
        => TargetDriver as IVulkanExplicitFrameTargetDriver
            ?? throw new InvalidOperationException(
                $"Vulkan target '{TargetDriver.ExecutionMode}' does not expose explicit target-frame submission.");

    /// <summary>
    /// Resolves the final-output attachments for a recording attempt without giving
    /// the output authority access to the command/resource renderer facade.
    /// </summary>
    internal SwapchainRecordingTarget ResolveRecordingTarget(
        in VulkanSwapchainRecordingTargetInput input)
        => VulkanSwapchainRecordingTargetResolver.Resolve(
            Desktop,
            in input);

    /// <summary>
    /// Captures the immutable desktop/UI attachment identities needed by a late
    /// dynamic-text overlay recording. Command recording must not reach back
    /// into output state while it is encoding native commands.
    /// </summary>
    internal bool TryCaptureDynamicUiOverlayTarget(
        uint imageIndex,
        out VulkanDynamicUiOverlayTarget target)
    {
        target = default;
        if (Desktop.Images is null || Desktop.ImageViews is null ||
            imageIndex >= Desktop.Images.Length || imageIndex >= Desktop.ImageViews.Length)
            return false;

        Image swapchainImage = Desktop.Images[imageIndex];
        ImageView swapchainView = Desktop.ImageViews[imageIndex];
        if (swapchainImage.Handle == 0 || swapchainView.Handle == 0)
            return false;

        Image streamlineImage = default;
        ImageView streamlineView = default;
        ImageLayout streamlineLayout = ImageLayout.Undefined;
        Image[]? streamlineImages = StreamlineUi.Images;
        ImageView[]? streamlineViews = StreamlineUi.ImageViews;
        bool[]? streamlineInitialized = StreamlineUi.ImagesInitialized;
        bool hasStreamlineUi = streamlineImages is not null &&
            streamlineViews is not null &&
            streamlineInitialized is not null &&
            imageIndex < streamlineImages.Length &&
            imageIndex < streamlineViews.Length &&
            imageIndex < streamlineInitialized.Length;
        if (hasStreamlineUi)
        {
            streamlineImage = streamlineImages![imageIndex];
            streamlineView = streamlineViews![imageIndex];
            hasStreamlineUi = streamlineImage.Handle != 0 && streamlineView.Handle != 0;
            if (hasStreamlineUi)
            {
                streamlineLayout = streamlineInitialized![imageIndex]
                    ? ImageLayout.General
                    : ImageLayout.Undefined;
            }
        }

        target = new VulkanDynamicUiOverlayTarget(
            swapchainImage,
            swapchainView,
            Desktop.Extent,
            hasStreamlineUi,
            streamlineImage,
            streamlineView,
            streamlineLayout);
        return true;
    }

    /// <summary>
    /// Captures the immutable Streamline UI attachment identity for a producer-owned
    /// primary recording attempt. Command recording must not query output state.
    /// </summary>
    internal bool TryCaptureStreamlineUiImage(
        uint imageIndex,
        out VulkanStreamlineImage image)
    {
        image = default;
        Image[]? images = StreamlineUi.Images;
        DeviceMemory[]? memories = StreamlineUi.ImageMemories;
        ImageView[]? views = StreamlineUi.ImageViews;
        bool[]? initialized = StreamlineUi.ImagesInitialized;
        if (images is null || memories is null || views is null ||
            imageIndex >= images.Length || imageIndex >= memories.Length ||
            imageIndex >= views.Length)
        {
            return false;
        }

        Image nativeImage = images[imageIndex];
        ImageView view = views[imageIndex];
        if (nativeImage.Handle == 0 || view.Handle == 0)
            return false;

        ImageLayout initialLayout = initialized is not null &&
            imageIndex < initialized.Length && initialized[imageIndex]
                ? ImageLayout.General
                : ImageLayout.Undefined;
        image = new VulkanStreamlineImage(
            nativeImage,
            memories[imageIndex],
            view,
            initialLayout,
            Desktop.ImageFormat,
            ImageUsageFlags.ColorAttachmentBit |
            ImageUsageFlags.SampledBit |
            ImageUsageFlags.TransferSrcBit |
            ImageUsageFlags.TransferDstBit,
            ImageAspectFlags.ColorBit,
            Desktop.Extent.Width,
            Desktop.Extent.Height,
            null);
        return true;
    }

    /// <summary>Publishes successful initialization of the Streamline UI target.</summary>
    internal void MarkStreamlineUiImageInitialized(uint imageIndex)
    {
        if (StreamlineUi.ImagesInitialized is not null &&
            imageIndex < StreamlineUi.ImagesInitialized.Length)
        {
            StreamlineUi.ImagesInitialized[imageIndex] = true;
        }
    }

    internal void DestroyBufferRaw(Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory)
    {
        VulkanTargetOutputContext context = _targetOutputContext
            ?? throw new InvalidOperationException(
                "The Vulkan target output context is not initialized.");
        context.DestroyBufferRaw(buffer, memory);
    }
}
