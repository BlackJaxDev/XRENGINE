using XREngine.Rendering.API.Rendering.OpenXR;
using VkFormat = Silk.NET.Vulkan.Format;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanXrGraphicsBinding
{
    private IOpenXrGraphicsHost Context => Host;

    private XRWindow? Window => Context.Window;
    private ulong _systemId => Context.SystemId;
    private uint _viewCount => Context.ViewCount;
    private ReadOnlySpan<OpenXrViewConfiguration> _viewConfigViews => Context.ViewConfigurationViews;
    private ReadOnlySpan<ulong> _swapchains => Context.Swapchains;
    private ReadOnlySpan<uint> _swapchainImageCounts => Context.SwapchainImageCounts;

    private IRuntimeRenderWorld? _openXrFrameWorld => Context.FrameWorld;
    private XRCamera? _openXrLeftEyeCamera => Context.LeftEyeCamera;
    private XRCamera? _openXrRightEyeCamera => Context.RightEyeCamera;
    private XRViewport? _openXrLeftViewport => Context.LeftViewport;
    private XRViewport? _openXrRightViewport => Context.RightViewport;
    private XRViewport? _openXrStereoViewport => Context.StereoViewport;

    private int _openXrPendingFrameNumber => Context.PendingFrameNumber;
    private ulong _openXrLastRenderedFrameId => Context.LastRenderedFrameId;
    private OpenXrStrictSpsFailureStage _strictSpsInjectedFailureStage
        => Context.StrictSpsInjectedFailureStage;

    private static bool OpenXrDebugLifecycle
        => RuntimeEngine.Rendering.Settings.OpenXrDebugLifecycle;

    private static bool OpenXrDebugClearOnly
        => RuntimeEngine.Rendering.Settings.OpenXrDebugClearOnly;

    private static bool OpenXrDebugRenderRightThenLeft
        => RuntimeEngine.Rendering.Settings.OpenXrDebugRenderRightThenLeft;

    private static bool VulkanCaptureEyeOutputs
        => string.Equals(
            Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanCaptureEyeOutputs),
            "1",
            StringComparison.Ordinal);

    private readonly XRTexture2D?[] _vulkanEyeMirrorColors = new XRTexture2D?[2];
    private readonly XRRenderBuffer?[] _vulkanEyeMirrorDepths = new XRRenderBuffer?[2];
    private readonly XRFrameBuffer?[] _vulkanEyeMirrorFbos = new XRFrameBuffer?[2];
    private uint _vulkanEyeMirrorWidth;
    private uint _vulkanEyeMirrorHeight;

    private XRTexture2DArray? _vulkanStereoColorArray;
    private XRTexture2DArray? _vulkanStereoDepthArray;
    private XRTexture2DArrayView? _vulkanStereoLeftColorView;
    private XRTexture2DArrayView? _vulkanStereoRightColorView;
    private XRFrameBuffer? _vulkanStereoFbo;
    private uint _vulkanStereoWidth;
    private uint _vulkanStereoHeight;
    private VkFormat _vulkanStereoColorFormat;

    private XRTexture2D? _previewLeftEyeTexture;
    private XRTexture2D? _previewRightEyeTexture;
    private uint _previewEyeTextureWidth;
    private uint _previewEyeTextureHeight;
    private EPixelInternalFormat _previewEyeTextureInternalFormat = EPixelInternalFormat.Rgba8;
    private ESizedInternalFormat _previewEyeTextureSizedFormat = ESizedInternalFormat.Rgba8;

    private XRTexture2D? _viewportMirrorColor;
    private XRRenderBuffer? _viewportMirrorDepth;
    private XRFrameBuffer? _viewportMirrorFbo;
    private uint _viewportMirrorWidth;
    private uint _viewportMirrorHeight;

    private readonly SwapchainImageVulkan2KHR*[] _swapchainImagesVK =
        new SwapchainImageVulkan2KHR*[RenderFrameViewSet.MaxViewCount];

    private int CheckResult(int result, string operation)
        => Context.CheckResult(result, operation);

    private bool TryResolveOpenXrFoveation(
        ERenderLibrary backend,
        out VrFoveationResolution resolution)
        => Context.TryResolveOpenXrFoveation(backend, out resolution);

    private ViewFoveationContext CreateOpenXrEyeFoveationContext(uint viewIndex)
        => Context.CreateOpenXrEyeFoveationContext(viewIndex);

    private void InitializeOpenXrViewsForActiveConfiguration(string backendLabel)
        => Context.InitializeOpenXrViewsForActiveConfiguration(backendLabel);

    private EVrOutputViewKind ResolveOpenXrRvcViewKind(uint viewIndex)
        => Context.ResolveOpenXrRvcViewKind(viewIndex);

    private bool IsLeftEyeLikeOpenXrView(uint viewIndex)
        => Context.IsLeftEyeLikeOpenXrView(viewIndex);

    private XRViewport? GetOpenXrEyeViewport(uint viewIndex)
        => Context.GetOpenXrEyeViewport(viewIndex);

    private XRCamera? GetOpenXrEyeCamera(uint viewIndex)
        => Context.GetOpenXrEyeCamera(viewIndex);

    private ulong GetOpenXrHistoryKey(EVrOutputViewKind kind)
        => Context.GetOpenXrHistoryKey(kind);

    private XRTexture2D? GetOpenXrPreviewTexture(uint viewIndex)
        => IsLeftEyeLikeOpenXrView(viewIndex)
            ? _previewLeftEyeTexture
            : _previewRightEyeTexture;

    private OpenXrEyeSwapchainExtent ResolveOpenXrEyeSwapchainExtent(uint viewIndex)
        => Context.ResolveOpenXrEyeSwapchainExtent(viewIndex);

    private uint GetOpenXrSwapchainWidth(uint viewIndex)
        => Context.GetOpenXrSwapchainWidth(viewIndex);

    private uint GetOpenXrSwapchainHeight(uint viewIndex)
        => Context.GetOpenXrSwapchainHeight(viewIndex);

    private void RecordOpenXrSwapchainExtent(uint viewIndex, uint width, uint height)
        => Context.RecordOpenXrSwapchainExtent(viewIndex, width, height);

    private void LogOpenXrEyeSwapchainExtent(
        string backend,
        uint viewIndex,
        OpenXrEyeSwapchainExtent extent)
        => Context.LogOpenXrEyeSwapchainExtent(backend, viewIndex, extent);

    private void EnsureOpenXrViewports(uint width, uint height)
        => Context.EnsureOpenXrViewports(width, height);

    private void EnsureOpenXrViewports(
        uint leftWidth,
        uint leftHeight,
        uint rightWidth,
        uint rightHeight)
        => Context.EnsureOpenXrViewports(leftWidth, leftHeight, rightWidth, rightHeight);

    private void EnsureOpenXrViewportExtent(
        XRViewport viewport,
        uint width,
        uint height)
        => Context.EnsureOpenXrViewportExtent(
            viewport,
            width,
            height);

    private void ApplyOpenXrEyePoseForRenderThread(uint viewIndex)
        => Context.ApplyOpenXrEyePoseForRenderThread(viewIndex);

    private RenderPipeline GetOrCreateOpenXrStereoPipeline(RenderPipeline? sourcePipeline)
        => Context.GetOrCreateOpenXrStereoPipeline(sourcePipeline);

    private void CopyPostProcessState(
        RenderPipeline sourcePipeline,
        RenderPipeline destinationPipeline,
        XRCamera sourceCamera,
        XRCamera destinationCamera)
        => Context.CopyPostProcessState(
            sourcePipeline,
            destinationPipeline,
            sourceCamera,
            destinationCamera);

    private void ReleaseOpenXrExternalEyeViewportPipelinesForTrueStereo()
        => Context.ReleaseOpenXrExternalEyeViewportPipelinesForTrueStereo();

    private void ReleaseOpenXrStereoViewportPipelineForExternalEyes()
        => Context.ReleaseOpenXrStereoViewportPipelineForExternalEyes();

    private void StageProjectionView(uint viewIndex)
        => Context.StageProjectionView(viewIndex);

    private void RecordSmokeViewRenderModeResolution(VrViewRenderModeResolution resolution)
        => Context.RecordSmokeViewRenderModeResolution(resolution);

    private void RecordSmokeSwapchain(
        string backend,
        int viewIndex,
        uint width,
        uint height,
        long format,
        uint sampleCount,
        uint imageCount)
        => Context.RecordSmokeSwapchain(
            backend,
            viewIndex,
            width,
            height,
            format,
            sampleCount,
            imageCount);

    private void RecordSmokeSwapchainsCreated()
        => Context.RecordSmokeSwapchainsCreated();

    private void RecordSmokeEyeAcquire(uint viewIndex, uint imageIndex)
        => Context.RecordSmokeEyeAcquire(viewIndex, imageIndex);

    private void RecordSmokeEyeWait(uint viewIndex)
        => Context.RecordSmokeEyeWait(viewIndex);

    private void RecordSmokeEyePublish(uint viewIndex)
        => Context.RecordSmokeEyePublish(viewIndex);

    private void RecordSmokeEyeRelease(uint viewIndex)
        => Context.RecordSmokeEyeRelease(viewIndex);

    private void RecordSmokeDesktopMirrorComposed()
        => Context.RecordSmokeDesktopMirrorComposed();

    private void RecordStrictSinglePassStereoSequentialFallbackAttempt(
        string stage,
        string reason)
        => Context.RecordStrictSinglePassStereoSequentialFallbackAttempt(stage, reason);

    private bool IsStrictSpsFailureInjectionEligible(OpenXrStrictSpsFailureStage stage)
        => Context.IsStrictSpsFailureInjectionEligible(stage);

    private bool TryCommitStrictSpsFailure(
        OpenXrStrictSpsFailureStage stage,
        string queueDisposition,
        out OpenXrStrictSpsFailureResolution resolution)
        => Context.TryCommitStrictSpsFailure(stage, queueDisposition, out resolution);

    private void RecordStrictSpsSuccessfulSubmission()
        => Context.RecordStrictSpsSuccessfulSubmission();

    private void RecordSmokeEffectiveTsrRenderScale(float? scale)
        => Context.RecordSmokeEffectiveTsrRenderScale(scale);

    private void RecordSmokeFailureOnce(string failure)
        => Context.RecordSmokeFailureOnce(failure);

    private bool ShouldLogLifecycle(int frameNumber)
        => Context.ShouldLogLifecycle(frameNumber);
}
