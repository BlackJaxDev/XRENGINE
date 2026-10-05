using XREngine.Rendering;

namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>OpenXR-owned state and engine view operations available to graphics bindings.</summary>
public interface IOpenXrGraphicsHost
{
    IOpenXrGraphicsCalls GraphicsCalls { get; }
    XRWindow? Window { get; }
    ulong InstanceHandle { get; }
    ulong SessionHandle { get; }
    ulong SystemId { get; }
    uint ViewCount { get; }
    ReadOnlySpan<OpenXrViewConfiguration> ViewConfigurationViews { get; }
    ReadOnlySpan<ulong> Swapchains { get; }
    ReadOnlySpan<uint> SwapchainImageCounts { get; }
    void SetSession(ulong sessionHandle);
    void SetSwapchain(uint viewIndex, ulong swapchainHandle, uint imageCount);
    ulong DetachSwapchain(uint viewIndex);
    bool HasAcquiredImage(ulong swapchainHandle);
    bool AreSwapchainImagesReleased(ReadOnlySpan<ulong> swapchainHandles);
    void AbandonAcquiredImagesAfterDeviceLoss();
    void AbandonActiveSwapchainsAfterDeviceLoss();
    bool TryReserveRetirement(int swapchainCount, out OpenXrRetirementToken token);
    void CancelRetirement(OpenXrRetirementToken token);
    void CommitRetirement(OpenXrRetirementToken token, ReadOnlySpan<ulong> swapchains);
    int DestroyRetiredSwapchain(OpenXrRetirementToken token, ulong swapchainHandle);
    void ReleaseRetirement(OpenXrRetirementToken token);
    void AbandonRetirement(OpenXrRetirementToken token);
    IOpenXrNativeGraphicsBorrow BorrowNativeGraphicsDispatch();
    /// <summary>Reports whether the current instance was created with the named extension enabled.</summary>
    bool IsInstanceExtensionEnabled(string extensionName);

    IRuntimeRenderWorld? FrameWorld { get; }
    XRCamera? LeftEyeCamera { get; }
    XRCamera? RightEyeCamera { get; }
    XRViewport? LeftViewport { get; }
    XRViewport? RightViewport { get; }
    XRViewport? StereoViewport { get; }
    int PendingFrameNumber { get; }
    ulong PendingFrameId { get; }
    long PendingPredictedDisplayTime { get; }
    bool PendingFrameUsesTrueSinglePassStereo { get; }
    ulong LastRenderedFrameId { get; }
    double CurrentRenderDeadlineMs { get; }
    OpenXrStrictSpsFailureStage StrictSpsInjectedFailureStage { get; }

    int CheckResult(int result, string operation);
    bool TryResolveOpenXrFoveation(ERenderLibrary backend, out VrFoveationResolution resolution);
    ViewFoveationContext CreateOpenXrEyeFoveationContext(uint viewIndex);
    void InitializeOpenXrViewsForActiveConfiguration(string backendLabel);
    EVrOutputViewKind ResolveOpenXrRvcViewKind(uint viewIndex);
    bool IsLeftEyeLikeOpenXrView(uint viewIndex);
    XRViewport? GetOpenXrEyeViewport(uint viewIndex);
    XRCamera? GetOpenXrEyeCamera(uint viewIndex);
    ulong GetOpenXrHistoryKey(EVrOutputViewKind kind);
    OpenXrEyeSwapchainExtent ResolveOpenXrEyeSwapchainExtent(uint viewIndex);
    uint GetOpenXrSwapchainWidth(uint viewIndex);
    uint GetOpenXrSwapchainHeight(uint viewIndex);
    void RecordOpenXrSwapchainExtent(uint viewIndex, uint width, uint height);
    void LogOpenXrEyeSwapchainExtent(string backend, uint viewIndex, OpenXrEyeSwapchainExtent extent);
    void EnsureOpenXrViewports(uint width, uint height);
    void EnsureOpenXrViewports(uint leftWidth, uint leftHeight, uint rightWidth, uint rightHeight);
    void EnsureOpenXrStereoViewport(uint width, uint height);
    void EnsureOpenXrViewportExtent(XRViewport viewport, uint width, uint height);
    void ApplyOpenXrEyePoseForRenderThread(uint viewIndex);
    RenderPipeline GetOrCreateOpenXrStereoPipeline(RenderPipeline? sourcePipeline);
    void CopyPostProcessState(RenderPipeline sourcePipeline, RenderPipeline destinationPipeline, XRCamera sourceCamera, XRCamera destinationCamera);
    void ReleaseOpenXrExternalEyeViewportPipelinesForTrueStereo();
    void ReleaseOpenXrStereoViewportPipelineForExternalEyes();
    void StageProjectionView(uint viewIndex);
    void RecordSmokeViewRenderModeResolution(VrViewRenderModeResolution resolution);
    void RecordSmokeSwapchain(string backend, int viewIndex, uint width, uint height, long format, uint sampleCount, uint imageCount);
    void RecordSmokeSwapchainsCreated();
    void RecordSmokeEyeAcquire(uint viewIndex, uint imageIndex);
    void RecordSmokeEyeWait(uint viewIndex);
    void RecordSmokeEyePublish(uint viewIndex);
    void RecordSmokeEyeRelease(uint viewIndex);
    void RecordSmokeDesktopMirrorComposed();
    void RecordStrictSinglePassStereoSequentialFallbackAttempt(string stage, string reason);
    bool IsStrictSpsFailureInjectionEligible(OpenXrStrictSpsFailureStage stage);
    bool TryCommitStrictSpsFailure(OpenXrStrictSpsFailureStage stage, string queueDisposition, out OpenXrStrictSpsFailureResolution resolution);
    void RecordStrictSpsSuccessfulSubmission();
    void RecordSmokeEffectiveTsrRenderScale(float? scale);
    void RecordSmokeFailureOnce(string failure);
    void RecordOpenXrLastRenderedFrameId(ulong frameId);
    bool ObserveSmokeRetiredGenerationCapacity(int count, int capacity, string admission);
    bool ShouldHoldSmokeRetiredGenerations();
    void RecordSmokeRetiredGenerationObservation(int count, int capacity, string admission);
    void ReleaseSmokeRetiredGenerationHoldForTerminalDrain();
    void WithSmokeDiagnosticsLock(Action action);
    T WithSmokeDiagnosticsLock<T>(Func<T> action);
    bool ShouldLogLifecycle(int frameNumber);
}
