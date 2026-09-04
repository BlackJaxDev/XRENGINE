using Silk.NET.OpenXR;
using XREngine.Rendering;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    private OpenXrGraphicsBindingHost? _graphicsBindingHost;

    /// <summary>
    /// Provides leaf graphics backends with narrowly scoped access to the
    /// backend-neutral OpenXR orchestration state owned by this API instance.
    /// </summary>
    internal OpenXrGraphicsBindingHost GraphicsBindingHost
        => _graphicsBindingHost ??= new(this);

    /// <summary>
    /// The backend-neutral host surface consumed by OpenXR graphics-binding
    /// implementations. This type is internal and shared with the renderer
    /// leaf assemblies through the runtime rendering assembly's IVT contract.
    /// </summary>
    internal sealed class OpenXrGraphicsBindingHost(OpenXRAPI owner)
    {
        internal XR Api => owner.Api;
        internal XRWindow? Window => owner.Window;
        internal ref Instance Instance => ref owner._instance;
        internal ref Session Session => ref owner._session;
        internal ulong SystemId => owner._systemId;
        internal uint ViewCount => owner._viewCount;
        internal ViewConfigurationView[] ViewConfigurationViews => owner._viewConfigViews;
        internal Swapchain[] Swapchains => owner._swapchains;
        internal uint[] SwapchainImageCounts => owner._swapchainImageCounts;

        internal IRuntimeRenderWorld? FrameWorld => owner._openXrFrameWorld;
        internal XRCamera? LeftEyeCamera => owner._openXrLeftEyeCamera;
        internal XRCamera? RightEyeCamera => owner._openXrRightEyeCamera;
        internal XRViewport? LeftViewport => owner._openXrLeftViewport;
        internal XRViewport? RightViewport => owner._openXrRightViewport;
        internal XRViewport? StereoViewport => owner._openXrStereoViewport;

        internal int PendingFrameNumber => owner._openXrPendingFrameNumber;
        internal ulong PendingFrameId => unchecked((ulong)Math.Max(0, Volatile.Read(ref owner._openXrPendingFrameNumber)));
        internal long PendingPredictedDisplayTime => owner._frameState.PredictedDisplayTime;
        internal bool PendingFrameUsesTrueSinglePassStereo
            => Volatile.Read(ref owner._pendingXrFrameUsesTrueSinglePassStereo) != 0;
        internal ulong LastRenderedFrameId => owner._openXrLastRenderedFrameId;
        internal double CurrentRenderDeadlineMs => owner.CurrentRenderDeadlineMs;
        internal OpenXrStrictSpsFailureStage StrictSpsInjectedFailureStage
            => owner._strictSpsInjectedFailureStage;

        internal Result CheckResult(Result result, string operation)
            => owner.CheckResult(result, operation);

        internal bool TryResolveOpenXrFoveation(
            ERenderLibrary backend,
            out VrFoveationResolution resolution)
            => owner.TryResolveOpenXrFoveation(backend, out resolution);

        internal ViewFoveationContext CreateOpenXrEyeFoveationContext(uint viewIndex)
            => owner.CreateOpenXrEyeFoveationContext(viewIndex);

        internal void InitializeOpenXrViewsForActiveConfiguration(string backendLabel)
            => owner.InitializeOpenXrViewsForActiveConfiguration(backendLabel);

        internal EVrOutputViewKind ResolveOpenXrRvcViewKind(uint viewIndex)
            => owner.ResolveOpenXrRvcViewKind(viewIndex);

        internal static bool IsLeftEyeLikeOpenXrView(uint viewIndex)
            => OpenXRAPI.IsLeftEyeLikeOpenXrView(viewIndex);

        internal XRViewport? GetOpenXrEyeViewport(uint viewIndex)
            => owner.GetOpenXrEyeViewport(viewIndex);

        internal XRCamera? GetOpenXrEyeCamera(uint viewIndex)
            => owner.GetOpenXrEyeCamera(viewIndex);

        internal static ulong GetOpenXrHistoryKey(EVrOutputViewKind kind)
            => OpenXRAPI.GetOpenXrHistoryKey(kind);

        internal OpenXrEyeSwapchainExtent ResolveOpenXrEyeSwapchainExtent(uint viewIndex)
            => owner.ResolveOpenXrEyeSwapchainExtent(viewIndex);

        internal uint GetOpenXrSwapchainWidth(uint viewIndex)
            => owner.GetOpenXrSwapchainWidth(viewIndex);

        internal uint GetOpenXrSwapchainHeight(uint viewIndex)
            => owner.GetOpenXrSwapchainHeight(viewIndex);

        internal void RecordOpenXrSwapchainExtent(uint viewIndex, uint width, uint height)
            => owner.RecordOpenXrSwapchainExtent(viewIndex, width, height);

        internal void LogOpenXrEyeSwapchainExtent(
            string backend,
            uint viewIndex,
            OpenXrEyeSwapchainExtent extent)
            => owner.LogOpenXrEyeSwapchainExtent(backend, viewIndex, extent);

        internal void EnsureOpenXrViewports(uint width, uint height)
            => owner.EnsureOpenXrViewports(width, height);

        internal void EnsureOpenXrViewports(
            uint leftWidth,
            uint leftHeight,
            uint rightWidth,
            uint rightHeight)
            => owner.EnsureOpenXrViewports(leftWidth, leftHeight, rightWidth, rightHeight);

        internal void EnsureOpenXrStereoViewport(uint width, uint height)
            => owner.EnsureOpenXrStereoViewport(width, height);

        internal static void EnsureOpenXrViewportExtent(
            XRViewport viewport,
            uint width,
            uint height)
            => OpenXRAPI.EnsureOpenXrViewportExtent(viewport, width, height);

        internal void ApplyOpenXrEyePoseForRenderThread(uint viewIndex)
            => owner.ApplyOpenXrEyePoseForRenderThread(viewIndex);

        internal RenderPipeline GetOrCreateOpenXrStereoPipeline(RenderPipeline? sourcePipeline)
            => owner.GetOrCreateOpenXrStereoPipeline(sourcePipeline);

        internal static void CopyPostProcessState(
            RenderPipeline sourcePipeline,
            RenderPipeline destinationPipeline,
            XRCamera sourceCamera,
            XRCamera destinationCamera)
            => OpenXRAPI.CopyPostProcessState(
                sourcePipeline,
                destinationPipeline,
                sourceCamera,
                destinationCamera);

        internal void ReleaseOpenXrExternalEyeViewportPipelinesForTrueStereo()
            => owner.ReleaseOpenXrExternalEyeViewportPipelinesForTrueStereo();

        internal void ReleaseOpenXrStereoViewportPipelineForExternalEyes()
            => owner.ReleaseOpenXrStereoViewportPipelineForExternalEyes();

        internal void FillProjectionView(
            uint viewIndex,
            CompositionLayerProjectionView* projectionViews)
            => owner.FillProjectionView(viewIndex, projectionViews);

        internal void RecordSmokeViewRenderModeResolution(VrViewRenderModeResolution resolution)
            => owner.RecordSmokeViewRenderModeResolution(resolution);

        internal void RecordSmokeSwapchain(
            string backend,
            int viewIndex,
            uint width,
            uint height,
            long format,
            uint sampleCount,
            uint imageCount)
            => owner.RecordSmokeSwapchain(
                backend,
                viewIndex,
                width,
                height,
                format,
                sampleCount,
                imageCount);

        internal void RecordSmokeSwapchainsCreated()
            => owner.RecordSmokeSwapchainsCreated();

        internal void RecordSmokeEyeAcquire(uint viewIndex, uint imageIndex)
            => owner.RecordSmokeEyeAcquire(viewIndex, imageIndex);

        internal void RecordSmokeEyeWait(uint viewIndex)
            => owner.RecordSmokeEyeWait(viewIndex);

        internal void RecordSmokeEyePublish(uint viewIndex)
            => owner.RecordSmokeEyePublish(viewIndex);

        internal void RecordSmokeEyeRelease(uint viewIndex)
            => owner.RecordSmokeEyeRelease(viewIndex);

        internal void RecordSmokeDesktopMirrorComposed()
            => owner.RecordSmokeDesktopMirrorComposed();

        internal void RecordStrictSinglePassStereoSequentialFallbackAttempt(
            string stage,
            string reason)
            => owner.RecordStrictSinglePassStereoSequentialFallbackAttempt(stage, reason);

        internal bool IsStrictSpsFailureInjectionEligible(OpenXrStrictSpsFailureStage stage)
            => owner.IsStrictSpsFailureInjectionEligible(stage);

        internal bool TryCommitStrictSpsFailure(
            OpenXrStrictSpsFailureStage stage,
            string queueDisposition,
            out OpenXrStrictSpsFailureResolution resolution)
            => owner.TryCommitStrictSpsFailure(stage, queueDisposition, out resolution);

        internal void RecordStrictSpsSuccessfulSubmission()
            => owner.RecordStrictSpsSuccessfulSubmission();

        internal void RecordSmokeEffectiveTsrRenderScale(float? scale)
            => owner.RecordSmokeEffectiveTsrRenderScale(scale);

        internal void RecordSmokeFailureOnce(string failure)
            => owner.RecordSmokeFailureOnce(failure);

        internal void RecordOpenXrLastRenderedFrameId(ulong frameId)
            => Volatile.Write(ref owner._openXrLastRenderedFrameId, frameId);

        internal void WithSmokeDiagnosticsLock(System.Action action)
        {
            lock (owner._smokeDiagnosticsLock)
                action();
        }

        internal T WithSmokeDiagnosticsLock<T>(Func<T> action)
        {
            lock (owner._smokeDiagnosticsLock)
                return action();
        }

        internal static bool ShouldLogLifecycle(int frameNumber)
            => OpenXRAPI.ShouldLogLifecycle(frameNumber);

    }
}
