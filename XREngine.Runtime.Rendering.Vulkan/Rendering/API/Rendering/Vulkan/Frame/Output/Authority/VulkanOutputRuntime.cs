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
internal sealed partial class VulkanOutputRuntime
{
    internal static readonly object Phase524bDesktopRejectionEvidenceLock = new();
    internal static OpenXrSmokeDesktopRejectionEvidence Phase524bDesktopRejectionEvidence = new();
    internal VulkanDesktopAcquireAvailabilityTracker _desktopAcquireAvailability;
    internal VulkanDesktopFrameFaultInjectionState _desktopFrameFaultInjection = new();
    internal VulkanDesktopSwapchainPolicyState _desktopSwapchainPolicy = new();
    internal int _hasPresentedCompleteSceneFrame;
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
    private long _explicitTargetFrameNumber;
    private int _imguiFrameMarkerResetRequested;

    internal VulkanOutputRuntime(VulkanTargetPolicySnapshot targetPolicy)
    {
        TargetPolicy = targetPolicy;
        OpenXrBackend = new VulkanOpenXrBackend();
        Desktop = new VulkanDesktopOutputState();
        Capture = new VulkanCaptureOutputState(
            screenshotReadbackSlotCount: 8,
            gpuStatsReadbackSlotCount: 32);
        ObsHook = new VulkanObsHookOutputState();
        StreamlineUi = new VulkanStreamlineUiOutputState();
        PresentationSource = new VulkanPresentationSourceState();
    }

    internal VulkanTargetPolicySnapshot TargetPolicy { get; }
    internal VulkanOpenXrBackend OpenXrBackend { get; }
    internal VulkanDesktopOutputState Desktop { get; }
    internal VulkanCaptureOutputState Capture { get; }
    internal VulkanObsHookOutputState ObsHook { get; }
    internal VulkanStreamlineUiOutputState StreamlineUi { get; }
    internal VulkanPresentationSourceState PresentationSource { get; }
    internal void StoreImGuiDrawData(ImGuiNET.ImDrawDataPtr drawData)
        => _imguiDrawData.Store(drawData);

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

    internal long NextExplicitTargetFrameNumber()
        => Interlocked.Increment(ref _explicitTargetFrameNumber);

    internal VulkanOutputRuntimeSnapshot CaptureSnapshot()
        => new(
            TargetPolicy.ExecutionMode,
            TargetPolicy.DriverName,
            TargetPolicy.HasExplicitFrameTarget,
            Volatile.Read(ref _explicitTargetFrameNumber));

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

}
