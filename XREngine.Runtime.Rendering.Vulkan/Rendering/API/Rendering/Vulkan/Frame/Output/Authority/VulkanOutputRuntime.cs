using System.Threading;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using XREngine.Rendering.DLSS;
using XREngine.Rendering.API.Rendering.OpenXR;

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
    internal VulkanImGuiDrawDataCache _imguiDrawData = new();
    internal VulkanImGuiResources _imguiResources = new();
    internal VulkanImGuiTextureRegistry _imguiTextureRegistry = new();
    internal readonly List<Fence> _orphanedSwapchainMarkerFences = new(2);
    internal Phase524bDesktopRejectionInjection _phase524bDesktopRejectionInjection = new();
    internal Phase524bDesktopRejectionDecision _phase524bPendingDesktopRejection;
    internal EVulkanRenderTargetMode _requestedRenderTargetMode = EVulkanRenderTargetMode.Auto;
    internal readonly List<RetiredOpenXrSubmissionFence> _retiredOpenXrSubmissionFences = new(2);
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
    private long _explicitTargetFrameNumber;
    private int _imguiFrameMarkerResetRequested;

    internal VulkanOutputRuntime(IVulkanRendererTargetDriver targetDriver)
    {
        TargetDriver = targetDriver;
        OpenXrBackend = new VulkanOpenXrBackend();
        ImGuiPlatformWindows = new VulkanImGuiPlatformWindowOutputAuthority();
        Desktop = new VulkanDesktopOutputState();
        Capture = new VulkanCaptureOutputState(readbackSlotCount: 8);
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

    internal void DestroyBufferRaw(Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory)
    {
        VulkanTargetOutputContext context = _targetOutputContext
            ?? throw new InvalidOperationException(
                "The Vulkan target output context is not initialized.");
        context.DestroyBufferRaw(buffer, memory);
    }
}
