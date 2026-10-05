using Silk.NET.OpenXR;
using Silk.NET.Core.Native;
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
    internal sealed class OpenXrGraphicsBindingHost(OpenXRAPI owner) : IOpenXrGraphicsHost
    {
        public IOpenXrGraphicsCalls GraphicsCalls => owner;
        public ulong InstanceHandle => owner._instance.Handle;
        public ulong SessionHandle => owner._session.Handle;
        public bool IsInstanceExtensionEnabled(string extensionName)
            => owner.IsInstanceExtensionEnabled(extensionName);
        ReadOnlySpan<OpenXrViewConfiguration> IOpenXrGraphicsHost.ViewConfigurationViews
            => owner._neutralViewConfigViews.AsSpan(0, checked((int)owner._viewCount));
        ReadOnlySpan<ulong> IOpenXrGraphicsHost.Swapchains
            => owner._neutralSwapchains.AsSpan(0, checked((int)owner._viewCount));
        ReadOnlySpan<uint> IOpenXrGraphicsHost.SwapchainImageCounts
            => owner._swapchainImageCounts.AsSpan(0, checked((int)owner._viewCount));

        public void SetSession(ulong sessionHandle)
        {
            if (sessionHandle == 0 || owner._session.Handle != 0)
                throw new InvalidOperationException("OpenXR session adoption requires one new live session.");
            owner._session = new Session(sessionHandle);
        }

        public void SetSwapchain(uint viewIndex, ulong swapchainHandle, uint imageCount)
        {
            if (viewIndex >= owner._viewCount)
                throw new ArgumentOutOfRangeException(nameof(viewIndex));
            if (swapchainHandle == 0 ||
                (owner._swapchains[viewIndex].Handle != 0 && owner._swapchains[viewIndex].Handle != swapchainHandle))
                throw new InvalidOperationException("OpenXR swapchain adoption requires one new live handle per view.");
            owner._swapchains[viewIndex] = new Swapchain(swapchainHandle);
            owner._neutralSwapchains[viewIndex] = swapchainHandle;
            owner._swapchainImageCounts[viewIndex] = imageCount;
        }

        public ulong DetachSwapchain(uint viewIndex)
        {
            if (viewIndex >= owner._viewCount)
                throw new ArgumentOutOfRangeException(nameof(viewIndex));
            ulong handle = owner._neutralSwapchains[viewIndex];
            if (handle != 0 && owner.HasAcquiredImage(handle))
                throw new InvalidOperationException("OpenXR swapchain still owns an acquired runtime image.");
            owner._neutralSwapchains[viewIndex] = 0;
            owner._swapchains[viewIndex] = default;
            owner._swapchainImageCounts[viewIndex] = 0;
            return handle;
        }

        public bool HasAcquiredImage(ulong swapchainHandle)
            => owner.HasAcquiredImage(swapchainHandle);

        public bool AreSwapchainImagesReleased(ReadOnlySpan<ulong> swapchainHandles)
            => owner.AreSwapchainImagesReleased(swapchainHandles);

        public void AbandonAcquiredImagesAfterDeviceLoss()
            => owner.AbandonAcquiredImagesAfterDeviceLoss();

        public void AbandonActiveSwapchainsAfterDeviceLoss()
            => owner.AbandonActiveSwapchainsAfterDeviceLoss();

        public bool TryReserveRetirement(int swapchainCount, out OpenXrRetirementToken token)
            => owner.TryReserveRetirement(swapchainCount, out token);

        public void CancelRetirement(OpenXrRetirementToken token)
            => owner.CancelRetirement(token);

        public void CommitRetirement(OpenXrRetirementToken token, ReadOnlySpan<ulong> swapchains)
            => owner.CommitRetirement(token, swapchains);

        public int DestroyRetiredSwapchain(OpenXrRetirementToken token, ulong swapchainHandle)
            => owner.DestroyRetiredSwapchain(token, swapchainHandle);

        public void ReleaseRetirement(OpenXrRetirementToken token)
            => owner.ReleaseRetirement(token);

        public void AbandonRetirement(OpenXrRetirementToken token)
            => owner.AbandonRetirement(token);

        public int CheckResult(int result, string operation)
            => (int)owner.CheckResult((Result)result, operation);

        bool IOpenXrGraphicsHost.IsLeftEyeLikeOpenXrView(uint viewIndex)
            => IsLeftEyeLikeOpenXrView(viewIndex);

        ulong IOpenXrGraphicsHost.GetOpenXrHistoryKey(EVrOutputViewKind kind)
            => GetOpenXrHistoryKey(kind);

        void IOpenXrGraphicsHost.EnsureOpenXrViewportExtent(XRViewport viewport, uint width, uint height)
            => EnsureOpenXrViewportExtent(viewport, width, height);

        void IOpenXrGraphicsHost.CopyPostProcessState(
            RenderPipeline sourcePipeline,
            RenderPipeline destinationPipeline,
            XRCamera sourceCamera,
            XRCamera destinationCamera)
            => CopyPostProcessState(sourcePipeline, destinationPipeline, sourceCamera, destinationCamera);

        bool IOpenXrGraphicsHost.ShouldLogLifecycle(int frameNumber)
            => ShouldLogLifecycle(frameNumber);

        public void StageProjectionView(uint viewIndex)
            => owner.StageProjectionView(viewIndex);

        public bool ObserveSmokeRetiredGenerationCapacity(int count, int capacity, string admission)
            => owner.ObserveSmokeRetiredGenerationCapacity(count, capacity, admission);

        public bool ShouldHoldSmokeRetiredGenerations()
            => owner.ShouldHoldSmokeRetiredGenerations();

        public void RecordSmokeRetiredGenerationObservation(int count, int capacity, string admission)
            => owner.RecordSmokeRetiredGenerationObservation(count, capacity, admission);

        public void ReleaseSmokeRetiredGenerationHoldForTerminalDrain()
            => owner.ReleaseSmokeRetiredGenerationHoldForTerminalDrain();

        public IOpenXrNativeGraphicsBorrow BorrowNativeGraphicsDispatch()
            => owner.BorrowNativeGraphicsDispatch();
        public XR Api => owner.Api;
        public XRWindow? Window => owner.Window;
        public ref Instance Instance => ref owner._instance;
        public ref Session Session => ref owner._session;
        public ulong SystemId => owner._systemId;
        public uint ViewCount => owner._viewCount;
        public ViewConfigurationView[] ViewConfigurationViews => owner._viewConfigViews;
        public Swapchain[] Swapchains => owner._swapchains;
        public uint[] SwapchainImageCounts => owner._swapchainImageCounts;

        public IRuntimeRenderWorld? FrameWorld => owner._openXrFrameWorld;
        public XRCamera? LeftEyeCamera => owner._openXrLeftEyeCamera;
        public XRCamera? RightEyeCamera => owner._openXrRightEyeCamera;
        public XRViewport? LeftViewport => owner._openXrLeftViewport;
        public XRViewport? RightViewport => owner._openXrRightViewport;
        public XRViewport? StereoViewport => owner._openXrStereoViewport;

        public int PendingFrameNumber => owner._openXrPendingFrameNumber;
        public ulong PendingFrameId => unchecked((ulong)Math.Max(0, Volatile.Read(ref owner._openXrPendingFrameNumber)));
        public long PendingPredictedDisplayTime => owner._frameState.PredictedDisplayTime;
        public bool PendingFrameUsesTrueSinglePassStereo
            => Volatile.Read(ref owner._pendingXrFrameUsesTrueSinglePassStereo) != 0;
        public ulong LastRenderedFrameId => owner._openXrLastRenderedFrameId;
        public double CurrentRenderDeadlineMs => owner.CurrentRenderDeadlineMs;
        public OpenXrStrictSpsFailureStage StrictSpsInjectedFailureStage
            => owner._strictSpsInjectedFailureStage;

        public Result CheckResult(Result result, string operation)
            => owner.CheckResult(result, operation);

        public bool TryResolveOpenXrFoveation(
            ERenderLibrary backend,
            out VrFoveationResolution resolution)
            => owner.TryResolveOpenXrFoveation(backend, out resolution);

        public ViewFoveationContext CreateOpenXrEyeFoveationContext(uint viewIndex)
            => owner.CreateOpenXrEyeFoveationContext(viewIndex);

        public void InitializeOpenXrViewsForActiveConfiguration(string backendLabel)
            => owner.InitializeOpenXrViewsForActiveConfiguration(backendLabel);

        public EVrOutputViewKind ResolveOpenXrRvcViewKind(uint viewIndex)
            => owner.ResolveOpenXrRvcViewKind(viewIndex);

        public static bool IsLeftEyeLikeOpenXrView(uint viewIndex)
            => OpenXRAPI.IsLeftEyeLikeOpenXrView(viewIndex);

        public XRViewport? GetOpenXrEyeViewport(uint viewIndex)
            => owner.GetOpenXrEyeViewport(viewIndex);

        public XRCamera? GetOpenXrEyeCamera(uint viewIndex)
            => owner.GetOpenXrEyeCamera(viewIndex);

        public static ulong GetOpenXrHistoryKey(EVrOutputViewKind kind)
            => OpenXRAPI.GetOpenXrHistoryKey(kind);

        public OpenXrEyeSwapchainExtent ResolveOpenXrEyeSwapchainExtent(uint viewIndex)
            => owner.ResolveOpenXrEyeSwapchainExtent(viewIndex);

        public uint GetOpenXrSwapchainWidth(uint viewIndex)
            => owner.GetOpenXrSwapchainWidth(viewIndex);

        public uint GetOpenXrSwapchainHeight(uint viewIndex)
            => owner.GetOpenXrSwapchainHeight(viewIndex);

        public void RecordOpenXrSwapchainExtent(uint viewIndex, uint width, uint height)
            => owner.RecordOpenXrSwapchainExtent(viewIndex, width, height);

        public void LogOpenXrEyeSwapchainExtent(
            string backend,
            uint viewIndex,
            OpenXrEyeSwapchainExtent extent)
            => owner.LogOpenXrEyeSwapchainExtent(backend, viewIndex, extent);

        public void EnsureOpenXrViewports(uint width, uint height)
            => owner.EnsureOpenXrViewports(width, height);

        public void EnsureOpenXrViewports(
            uint leftWidth,
            uint leftHeight,
            uint rightWidth,
            uint rightHeight)
            => owner.EnsureOpenXrViewports(leftWidth, leftHeight, rightWidth, rightHeight);

        public void EnsureOpenXrStereoViewport(uint width, uint height)
            => owner.EnsureOpenXrStereoViewport(width, height);

        public static void EnsureOpenXrViewportExtent(
            XRViewport viewport,
            uint width,
            uint height)
            => OpenXRAPI.EnsureOpenXrViewportExtent(viewport, width, height);

        public void ApplyOpenXrEyePoseForRenderThread(uint viewIndex)
            => owner.ApplyOpenXrEyePoseForRenderThread(viewIndex);

        public RenderPipeline GetOrCreateOpenXrStereoPipeline(RenderPipeline? sourcePipeline)
            => owner.GetOrCreateOpenXrStereoPipeline(sourcePipeline);

        public static void CopyPostProcessState(
            RenderPipeline sourcePipeline,
            RenderPipeline destinationPipeline,
            XRCamera sourceCamera,
            XRCamera destinationCamera)
            => OpenXRAPI.CopyPostProcessState(
                sourcePipeline,
                destinationPipeline,
                sourceCamera,
                destinationCamera);

        public void ReleaseOpenXrExternalEyeViewportPipelinesForTrueStereo()
            => owner.ReleaseOpenXrExternalEyeViewportPipelinesForTrueStereo();

        public void ReleaseOpenXrStereoViewportPipelineForExternalEyes()
            => owner.ReleaseOpenXrStereoViewportPipelineForExternalEyes();

        public void FillProjectionView(
            uint viewIndex,
            CompositionLayerProjectionView* projectionViews)
            => owner.FillProjectionView(viewIndex, projectionViews);

        public void RecordSmokeViewRenderModeResolution(VrViewRenderModeResolution resolution)
            => owner.RecordSmokeViewRenderModeResolution(resolution);

        public void RecordSmokeSwapchain(
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

        public void RecordSmokeSwapchainsCreated()
            => owner.RecordSmokeSwapchainsCreated();

        public void RecordSmokeEyeAcquire(uint viewIndex, uint imageIndex)
            => owner.RecordSmokeEyeAcquire(viewIndex, imageIndex);

        public void RecordSmokeEyeWait(uint viewIndex)
            => owner.RecordSmokeEyeWait(viewIndex);

        public void RecordSmokeEyePublish(uint viewIndex)
            => owner.RecordSmokeEyePublish(viewIndex);

        public void RecordSmokeEyeRelease(uint viewIndex)
            => owner.RecordSmokeEyeRelease(viewIndex);

        public void RecordSmokeDesktopMirrorComposed()
            => owner.RecordSmokeDesktopMirrorComposed();

        public void RecordStrictSinglePassStereoSequentialFallbackAttempt(
            string stage,
            string reason)
            => owner.RecordStrictSinglePassStereoSequentialFallbackAttempt(stage, reason);

        public bool IsStrictSpsFailureInjectionEligible(OpenXrStrictSpsFailureStage stage)
            => owner.IsStrictSpsFailureInjectionEligible(stage);

        public bool TryCommitStrictSpsFailure(
            OpenXrStrictSpsFailureStage stage,
            string queueDisposition,
            out OpenXrStrictSpsFailureResolution resolution)
            => owner.TryCommitStrictSpsFailure(stage, queueDisposition, out resolution);

        public void RecordStrictSpsSuccessfulSubmission()
            => owner.RecordStrictSpsSuccessfulSubmission();

        public void RecordSmokeEffectiveTsrRenderScale(float? scale)
            => owner.RecordSmokeEffectiveTsrRenderScale(scale);

        public void RecordSmokeFailureOnce(string failure)
            => owner.RecordSmokeFailureOnce(failure);

        public void RecordOpenXrLastRenderedFrameId(ulong frameId)
            => Volatile.Write(ref owner._openXrLastRenderedFrameId, frameId);

        public void WithSmokeDiagnosticsLock(System.Action action)
        {
            lock (owner._smokeDiagnosticsLock)
                action();
        }

        public T WithSmokeDiagnosticsLock<T>(Func<T> action)
        {
            lock (owner._smokeDiagnosticsLock)
                return action();
        }

        public static bool ShouldLogLifecycle(int frameNumber)
            => OpenXRAPI.ShouldLogLifecycle(frameNumber);

    }
}
