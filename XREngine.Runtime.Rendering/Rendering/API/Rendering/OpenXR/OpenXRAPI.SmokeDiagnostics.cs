using Silk.NET.OpenXR;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using XREngine.Rendering;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    private readonly object _smokeDiagnosticsLock = new();
    private readonly List<string> _smokeRuntimeStateTransitions = [];
    private readonly List<string> _smokeSessionStateTransitions = [];
    private readonly List<string> _smokeWarnings = [];
    private readonly List<string> _smokeFailures = [];
    private readonly List<OpenXrSmokeSwapchainSummary> _smokeSwapchains = [];
    private readonly long[] _smokePerEyeAcquireCounts = new long[RenderFrameViewSet.MaxViewCount];
    private readonly long[] _smokePerEyeWaitCounts = new long[RenderFrameViewSet.MaxViewCount];
    private readonly long[] _smokePerEyePublishCounts = new long[RenderFrameViewSet.MaxViewCount];
    private readonly long[] _smokePerEyeReleaseCounts = new long[RenderFrameViewSet.MaxViewCount];
    private readonly int[] _smokePerEyeLastImageSlots = new int[RenderFrameViewSet.MaxViewCount];
    private string[] _smokeEnabledExtensions = [];
    private VrViewRenderModeResolution _smokeViewRenderModeResolution;
    private int _smokeViewRenderModeResolutionObserved;
    private VrFoveationResolution _smokeFoveationResolution;
    private string[] _smokeFoveationBackendCapabilities = [];
    private string _smokeRendererBackend = string.Empty;
    private string _smokeReferenceSpaceType = string.Empty;
    private int _smokeInstanceCreated;
    private int _smokeSystemFound;
    private int _smokeSessionCreated;
    private int _smokeReferenceSpaceCreated;
    private int _smokeSwapchainsCreated;
    private int _smokeSessionRunning;
    private int _smokeTeardownCompleted;
    private int _smokePredictedViewPoseCached;
    private int _smokeLateViewPoseCached;
    private int _smokePredictedActionPoseCacheUpdated;
    private int _smokeLateActionPoseCacheUpdated;
    private int _smokeDesktopMirrorComposed;
    private long _smokeSubmittedFrameCount;
    private long _smokeNoLayerFrameCount;
    private long _smokeEndFrameFailureCount;
    private long _smokeMissedDeadlineCount;
    private long _strictSinglePassStereoSequentialFallbackAttemptCount;
    private readonly OpenXrStrictSpsFailureStage _strictSpsInjectedFailureStage =
        OpenXrStrictSpsFailurePolicy.ResolveInjectedStage();
    private readonly long _strictSpsInjectedFailureWarmupFrameCount =
        OpenXrStrictSpsFailurePolicy.ResolveInjectedFailureWarmupFrameCount();
    private long _strictSpsInjectedFailureCount;
    private long _strictSpsInjectedFallbackBaseline;
    private int _strictSpsInjectedFailureHandled;
    private uint _strictSpsInjectedProjectionLayerCount;
    private int _strictSpsInjectedSequentialFallbackRequested;
    private long _strictSpsInjectedCompletedFrameCount;
    private string _strictSpsInjectedQueueDisposition = string.Empty;
    private long _strictSpsSuccessfulSubmissionCount;
    private uint _smokeLocatedViewCount;
    private int _smokeLastEndFrameResult;
    private uint _smokeLastEndFrameLayerCount;
    private int _smokeEffectiveTsrRenderScaleBits = BitConverter.SingleToInt32Bits(float.NaN);

    public long SmokeSubmittedFrameCount => Volatile.Read(ref _smokeSubmittedFrameCount);
    public long SmokeNoLayerFrameCount => Volatile.Read(ref _smokeNoLayerFrameCount);
    public long SmokeCompletedFrameCount => SmokeSubmittedFrameCount + SmokeNoLayerFrameCount;
    public long SmokeEndFrameFailureCount => Volatile.Read(ref _smokeEndFrameFailureCount);
    public long StrictSinglePassStereoSequentialFallbackAttemptCount
        => Volatile.Read(ref _strictSinglePassStereoSequentialFallbackAttemptCount);
    public bool SmokeTeardownCompleted => Volatile.Read(ref _smokeTeardownCompleted) != 0;
    public event Action<long, long, long>? SmokeFrameCompleted;

    public long GetSmokeEyeAcquireCount(uint viewIndex)
        => ReadEyeCounter(_smokePerEyeAcquireCounts, viewIndex);

    public long GetSmokeEyeWaitCount(uint viewIndex)
        => ReadEyeCounter(_smokePerEyeWaitCounts, viewIndex);

    public long GetSmokeEyePublishCount(uint viewIndex)
        => ReadEyeCounter(_smokePerEyePublishCounts, viewIndex);

    public long GetSmokeEyeReleaseCount(uint viewIndex)
        => ReadEyeCounter(_smokePerEyeReleaseCounts, viewIndex);

    public int GetSmokeEyeLastImageSlot(uint viewIndex)
        => viewIndex < _smokePerEyeLastImageSlots.Length
            ? Volatile.Read(ref _smokePerEyeLastImageSlots[viewIndex])
            : -1;

    public int SmokeLastEndFrameResult => Volatile.Read(ref _smokeLastEndFrameResult);
    public uint SmokeLastEndFrameLayerCount => Volatile.Read(ref _smokeLastEndFrameLayerCount);
    public ulong SmokeLastRenderedFrameId => Volatile.Read(ref _openXrLastRenderedFrameId);
    public float? SmokeEffectiveTsrRenderScale
    {
        get
        {
            float value = BitConverter.Int32BitsToSingle(
                Volatile.Read(ref _smokeEffectiveTsrRenderScaleBits));
            return float.IsFinite(value) ? value : null;
        }
    }

    public OpenXrSmokeSummary CreateSmokeSummary(string? logDirectory = null)
    {
        string? runtimeManifestPath = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson);
        if (string.IsNullOrWhiteSpace(runtimeManifestPath))
            runtimeManifestPath = TryGetOpenXRActiveRuntime();

        var (runtimeName, runtimeVersion) = TryReadRuntimeManifestMetadata(runtimeManifestPath);

        lock (_smokeDiagnosticsLock)
        {
            string[] failures = BuildSmokeFailuresForSummary();
            return new OpenXrSmokeSummary
            {
                CapturedAtUtc = DateTimeOffset.UtcNow,
                LogDirectory = logDirectory,
                RuntimeManifestPath = runtimeManifestPath,
                RuntimeName = runtimeName,
                RuntimeVersion = runtimeVersion,
                RendererBackend = _smokeRendererBackend,
                RuntimeState = _runtimeState.ToString(),
                SessionState = _sessionState.ToString(),
                ReferenceSpaceType = _smokeReferenceSpaceType,
                ViewRenderModeRequested = _smokeViewRenderModeResolution.RequestedMode.ToString(),
                ViewRenderModeEffective = _smokeViewRenderModeResolution.EffectiveMode.ToString(),
                ViewRenderImplementationPath = _smokeViewRenderModeResolution.EffectiveImplementationPath.ToString(),
                ViewRenderModeResolutionObserved = Volatile.Read(ref _smokeViewRenderModeResolutionObserved) != 0,
                ViewRenderTemporalHistoryPolicy = _smokeViewRenderModeResolution.TemporalHistoryPolicy.ToString(),
                ViewRenderModeSupported = _smokeViewRenderModeResolution.IsSupported,
                ViewRenderModeDiagnostic = _smokeViewRenderModeResolution.Diagnostic,
                FoveationRequestedMode = _smokeFoveationResolution.RequestedMode.ToString(),
                FoveationEffectiveMode = _smokeFoveationResolution.EffectiveMode.ToString(),
                FoveationQualityPreset = _smokeFoveationResolution.QualityPreset.ToString(),
                FoveationCapabilityPath = _smokeFoveationResolution.CapabilityPath.ToString(),
                FoveationSupported = _smokeFoveationResolution.IsSupported,
                FoveationDiagnostic = _smokeFoveationResolution.Diagnostic,
                FoveationBackendCapabilities = [.. _smokeFoveationBackendCapabilities],
                EnabledExtensions = [.. _smokeEnabledExtensions],
                InstanceCreated = Volatile.Read(ref _smokeInstanceCreated) != 0,
                SystemFound = Volatile.Read(ref _smokeSystemFound) != 0,
                SessionCreated = Volatile.Read(ref _smokeSessionCreated) != 0,
                ReferenceSpaceCreated = Volatile.Read(ref _smokeReferenceSpaceCreated) != 0,
                SwapchainsCreated = Volatile.Read(ref _smokeSwapchainsCreated) != 0,
                SessionRunning = Volatile.Read(ref _smokeSessionRunning) != 0,
                TeardownCompleted = Volatile.Read(ref _smokeTeardownCompleted) != 0,
                SubmittedFrameCount = Volatile.Read(ref _smokeSubmittedFrameCount),
                NoLayerFrameCount = Volatile.Read(ref _smokeNoLayerFrameCount),
                EndFrameFailureCount = Volatile.Read(ref _smokeEndFrameFailureCount),
                StrictSinglePassStereoSequentialFallbackAttemptCount = Volatile.Read(ref _strictSinglePassStereoSequentialFallbackAttemptCount),
                StrictSpsInjectedFailureStage = _strictSpsInjectedFailureStage.ToString(),
                StrictSpsInjectedFailureCount = Volatile.Read(ref _strictSpsInjectedFailureCount),
                StrictSpsInjectedFailureHandled = Volatile.Read(ref _strictSpsInjectedFailureHandled) != 0,
                StrictSpsInjectedProjectionLayerCount = Volatile.Read(ref _strictSpsInjectedProjectionLayerCount),
                StrictSpsInjectedSequentialFallbackRequested = Volatile.Read(ref _strictSpsInjectedSequentialFallbackRequested) != 0,
                StrictSpsInjectedSequentialFallbackAttemptDelta = Math.Max(
                    0L,
                    Volatile.Read(ref _strictSinglePassStereoSequentialFallbackAttemptCount) -
                    Volatile.Read(ref _strictSpsInjectedFallbackBaseline)),
                StrictSpsInjectedCompletedFrameCount = Volatile.Read(ref _strictSpsInjectedCompletedFrameCount),
                StrictSpsInjectedQueueDisposition = _strictSpsInjectedQueueDisposition,
                StrictSpsSuccessfulSubmissionCount = Volatile.Read(ref _strictSpsSuccessfulSubmissionCount),
                LocatedViewCount = Volatile.Read(ref _smokeLocatedViewCount),
                PredictedViewPoseCached = Volatile.Read(ref _smokePredictedViewPoseCached) != 0,
                LateViewPoseCached = Volatile.Read(ref _smokeLateViewPoseCached) != 0,
                PredictedActionPoseCacheUpdated = Volatile.Read(ref _smokePredictedActionPoseCacheUpdated) != 0,
                LateActionPoseCacheUpdated = Volatile.Read(ref _smokeLateActionPoseCacheUpdated) != 0,
                LeftControllerGripPoseAvailable = Volatile.Read(ref _openXrPredLeftControllerValid) != 0 || Volatile.Read(ref _openXrLateLeftControllerValid) != 0,
                RightControllerGripPoseAvailable = Volatile.Read(ref _openXrPredRightControllerValid) != 0 || Volatile.Read(ref _openXrLateRightControllerValid) != 0,
                LeftControllerAimPoseAvailable = Volatile.Read(ref _openXrPredLeftControllerAimValid) != 0 || Volatile.Read(ref _openXrLateLeftControllerAimValid) != 0,
                RightControllerAimPoseAvailable = Volatile.Read(ref _openXrPredRightControllerAimValid) != 0 || Volatile.Read(ref _openXrLateRightControllerAimValid) != 0,
                TrackerPoseAvailable = HasAnyTrackerPoseAvailable(),
                KnownTrackerUserPaths = GetKnownTrackerUserPaths(),
                LeftHandJointsActive = Volatile.Read(ref _leftHandJointsActive) != 0,
                RightHandJointsActive = Volatile.Read(ref _rightHandJointsActive) != 0,
                DesktopMirrorComposed = Volatile.Read(ref _smokeDesktopMirrorComposed) != 0,
                MissedDeadlineCount = Volatile.Read(ref _smokeMissedDeadlineCount),
                PerEyeAcquireCounts = CopyCounterArray(_smokePerEyeAcquireCounts),
                PerEyeWaitCounts = CopyCounterArray(_smokePerEyeWaitCounts),
                PerEyePublishCounts = CopyCounterArray(_smokePerEyePublishCounts),
                PerEyeReleaseCounts = CopyCounterArray(_smokePerEyeReleaseCounts),
                PerFrameAllocationsBytes = 0,
                Swapchains = [.. _smokeSwapchains],
                RuntimeStateTransitions = [.. _smokeRuntimeStateTransitions],
                SessionStateTransitions = [.. _smokeSessionStateTransitions],
                Warnings = [.. _smokeWarnings],
                Failures = failures,
            };
        }
    }

    private string[] BuildSmokeFailuresForSummary()
    {
        List<string> failures = [.. _smokeFailures];
        if (!_smokeViewRenderModeResolution.IsSupported &&
            !string.IsNullOrWhiteSpace(_smokeViewRenderModeResolution.Diagnostic))
        {
            AddFailureIfMissing(
                failures,
                $"Unsupported VR.ViewRenderMode={_smokeViewRenderModeResolution.RequestedMode}. {_smokeViewRenderModeResolution.Diagnostic}");
        }

        if (!_smokeFoveationResolution.IsSupported &&
            RuntimeRenderingHostServices.Current.VrFoveationRequireRequested &&
            !string.IsNullOrWhiteSpace(_smokeFoveationResolution.Diagnostic))
        {
            AddFailureIfMissing(
                failures,
                $"Unsupported VR.Foveation.Mode={_smokeFoveationResolution.RequestedMode}. {_smokeFoveationResolution.Diagnostic}");
        }

        long strictFallbackAttempts = Volatile.Read(ref _strictSinglePassStereoSequentialFallbackAttemptCount);
        if (strictFallbackAttempts != 0)
        {
            AddFailureIfMissing(
                failures,
                $"Strict SinglePassStereo attempted sequential fallback {strictFallbackAttempts} time(s); expected exactly zero.");
        }

        return [.. failures];
    }

    private static void AddFailureIfMissing(List<string> failures, string failure)
    {
        foreach (string existing in failures)
        {
            if (string.Equals(existing, failure, StringComparison.Ordinal))
                return;
        }

        failures.Add(failure);
    }

    public void RequestSmokeSessionExit()
    {
        if (_session.Handle == 0)
            return;

        try
        {
            Result result = CheckResult(Api.RequestExitSession(_session), "xrRequestExitSession");
            if (result != Result.Success)
                RecordSmokeFailure($"xrRequestExitSession returned {result}.");
        }
        catch (Exception ex)
        {
            RecordSmokeFailure($"xrRequestExitSession threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ResetSmokeDiagnostics()
    {
        ResetStrictSpsBoundaryCaptureDiagnostics();
        lock (_smokeDiagnosticsLock)
        {
            _smokeRuntimeStateTransitions.Clear();
            _smokeSessionStateTransitions.Clear();
            _smokeWarnings.Clear();
            _smokeFailures.Clear();
            _smokeSwapchains.Clear();
            _smokeEnabledExtensions = [];
            _smokeViewRenderModeResolution = default;
            Volatile.Write(ref _smokeViewRenderModeResolutionObserved, 0);
            _smokeFoveationResolution = default;
            _smokeFoveationBackendCapabilities = [];
            _smokeRendererBackend = string.Empty;
            _smokeReferenceSpaceType = string.Empty;
        }

        Array.Clear(_smokePerEyeAcquireCounts);
        Array.Clear(_smokePerEyeWaitCounts);
        Array.Clear(_smokePerEyePublishCounts);
        Array.Clear(_smokePerEyeReleaseCounts);
        Array.Fill(_smokePerEyeLastImageSlots, -1);
        Volatile.Write(ref _smokeInstanceCreated, 0);
        Volatile.Write(ref _smokeSystemFound, 0);
        Volatile.Write(ref _smokeSessionCreated, 0);
        Volatile.Write(ref _smokeReferenceSpaceCreated, 0);
        Volatile.Write(ref _smokeSwapchainsCreated, 0);
        Volatile.Write(ref _smokeSessionRunning, 0);
        Volatile.Write(ref _smokeTeardownCompleted, 0);
        Volatile.Write(ref _smokePredictedViewPoseCached, 0);
        Volatile.Write(ref _smokeLateViewPoseCached, 0);
        Volatile.Write(ref _smokePredictedActionPoseCacheUpdated, 0);
        Volatile.Write(ref _smokeLateActionPoseCacheUpdated, 0);
        Volatile.Write(ref _smokeDesktopMirrorComposed, 0);
        Volatile.Write(ref _smokeSubmittedFrameCount, 0);
        Volatile.Write(ref _smokeNoLayerFrameCount, 0);
        Volatile.Write(ref _smokeEndFrameFailureCount, 0);
        Volatile.Write(ref _smokeMissedDeadlineCount, 0);
        Volatile.Write(ref _strictSinglePassStereoSequentialFallbackAttemptCount, 0);
        Volatile.Write(ref _strictSpsInjectedFailureCount, 0);
        Volatile.Write(ref _strictSpsInjectedFallbackBaseline, 0);
        Volatile.Write(ref _strictSpsInjectedFailureHandled, 0);
        Volatile.Write(ref _strictSpsInjectedProjectionLayerCount, 0u);
        Volatile.Write(ref _strictSpsInjectedSequentialFallbackRequested, 0);
        Volatile.Write(ref _strictSpsInjectedCompletedFrameCount, 0L);
        _strictSpsInjectedQueueDisposition = string.Empty;
        Volatile.Write(ref _strictSpsSuccessfulSubmissionCount, 0L);
        Volatile.Write(ref _smokeLocatedViewCount, 0);
        Volatile.Write(ref _smokeLastEndFrameResult, (int)Result.Success);
        Volatile.Write(ref _smokeLastEndFrameLayerCount, 0u);
        Volatile.Write(ref _openXrLastRenderedFrameId, 0UL);
        Volatile.Write(
            ref _smokeEffectiveTsrRenderScaleBits,
            BitConverter.SingleToInt32Bits(float.NaN));
    }

    private void RecordSmokeInstanceCreated(string rendererBackend, string[] enabledExtensions)
    {
        lock (_smokeDiagnosticsLock)
        {
            _smokeRendererBackend = rendererBackend;
            _smokeEnabledExtensions = enabledExtensions;
        }

        Volatile.Write(ref _smokeInstanceCreated, 1);
    }

    private void RecordSmokeViewRenderModeResolution(VrViewRenderModeResolution resolution)
    {
        lock (_smokeDiagnosticsLock)
            _smokeViewRenderModeResolution = resolution;
        Volatile.Write(ref _smokeViewRenderModeResolutionObserved, 1);
    }

    private void RecordSmokeFoveationResolution(
        VrFoveationResolution resolution,
        VrFoveationBackendCapabilities capabilities)
    {
        lock (_smokeDiagnosticsLock)
        {
            _smokeFoveationResolution = resolution;
            _smokeFoveationBackendCapabilities = BuildFoveationCapabilityNames(capabilities);
        }
    }

    private void RecordSmokeSystemFound()
        => Volatile.Write(ref _smokeSystemFound, 1);

    private void RecordSmokeSessionCreated(string rendererBackend)
    {
        lock (_smokeDiagnosticsLock)
            _smokeRendererBackend = rendererBackend;

        Volatile.Write(ref _smokeSessionCreated, 1);
    }

    private void RecordSmokeReferenceSpaceCreated(ReferenceSpaceType referenceSpaceType)
    {
        lock (_smokeDiagnosticsLock)
            _smokeReferenceSpaceType = referenceSpaceType.ToString();

        Volatile.Write(ref _smokeReferenceSpaceCreated, 1);
    }

    private void RecordSmokeSwapchain(
        string backend,
        int viewIndex,
        uint width,
        uint height,
        long format,
        uint sampleCount,
        uint imageCount)
    {
        lock (_smokeDiagnosticsLock)
        {
            _smokeSwapchains.RemoveAll(s => s.ViewIndex == viewIndex && string.Equals(s.Backend, backend, StringComparison.Ordinal));
            _smokeSwapchains.Add(new OpenXrSmokeSwapchainSummary
            {
                Backend = backend,
                ViewIndex = viewIndex,
                Width = width,
                Height = height,
                Format = format,
                SampleCount = sampleCount,
                ImageCount = imageCount,
            });
        }
    }

    private void RecordSmokeSwapchainsCreated()
        => Volatile.Write(ref _smokeSwapchainsCreated, 1);

    private void RecordSmokeRuntimeState(OpenXrRuntimeState state)
    {
        Volatile.Write(ref _smokeSessionRunning, state == OpenXrRuntimeState.SessionRunning ? 1 : 0);
        lock (_smokeDiagnosticsLock)
            _smokeRuntimeStateTransitions.Add($"{DateTimeOffset.UtcNow:O} {state}");
    }

    private void RecordSmokeSessionState(SessionState state)
    {
        lock (_smokeDiagnosticsLock)
            _smokeSessionStateTransitions.Add($"{DateTimeOffset.UtcNow:O} {state}");
    }

    private void RecordSmokeLocatedViews(uint viewCountOutput)
        => Volatile.Write(ref _smokeLocatedViewCount, viewCountOutput);

    private void RecordSmokeViewPoseCache(OpenXrPoseTiming timing)
    {
        if (timing == OpenXrPoseTiming.Late)
            Volatile.Write(ref _smokeLateViewPoseCached, 1);
        else
            Volatile.Write(ref _smokePredictedViewPoseCached, 1);
    }

    private void RecordSmokeActionPoseCache(OpenXrPoseTiming timing)
    {
        if (timing == OpenXrPoseTiming.Late)
            Volatile.Write(ref _smokeLateActionPoseCacheUpdated, 1);
        else
            Volatile.Write(ref _smokePredictedActionPoseCacheUpdated, 1);
    }

    private void RecordSmokeEyeAcquire(uint viewIndex, uint imageIndex)
    {
        IncrementEyeCounter(_smokePerEyeAcquireCounts, viewIndex);
        if (viewIndex < _smokePerEyeLastImageSlots.Length)
            Volatile.Write(ref _smokePerEyeLastImageSlots[viewIndex], checked((int)imageIndex));
    }

    private void RecordSmokeEyeWait(uint viewIndex)
        => IncrementEyeCounter(_smokePerEyeWaitCounts, viewIndex);

    private void RecordSmokeEyePublish(uint viewIndex)
        => IncrementEyeCounter(_smokePerEyePublishCounts, viewIndex);

    private void RecordSmokeEyeRelease(uint viewIndex)
        => IncrementEyeCounter(_smokePerEyeReleaseCounts, viewIndex);

    private void RecordSmokeEndFrame(Result result, uint layerCount)
    {
        Volatile.Write(ref _smokeLastEndFrameResult, (int)result);
        Volatile.Write(ref _smokeLastEndFrameLayerCount, layerCount);
        if (result == Result.Success)
        {
            if (layerCount > 0)
                Interlocked.Increment(ref _smokeSubmittedFrameCount);
            else
                Interlocked.Increment(ref _smokeNoLayerFrameCount);
        }
        else
        {
            Interlocked.Increment(ref _smokeEndFrameFailureCount);
        }

        long submitted = Volatile.Read(ref _smokeSubmittedFrameCount);
        long noLayer = Volatile.Read(ref _smokeNoLayerFrameCount);
        try
        {
            SmokeFrameCompleted?.Invoke(submitted + noLayer, submitted, noLayer);
        }
        catch (Exception ex)
        {
            RecordSmokeFailureOnce($"Smoke frame-ledger callback failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void RecordSmokeDesktopMirrorComposed()
        => Volatile.Write(ref _smokeDesktopMirrorComposed, 1);

    private void RecordSmokeMissedDeadline()
        => Interlocked.Increment(ref _smokeMissedDeadlineCount);

    private void RecordStrictSinglePassStereoSequentialFallbackAttempt(string stage, string reason)
    {
        Interlocked.Increment(ref _strictSinglePassStereoSequentialFallbackAttemptCount);
        string message = $"Strict SinglePassStereo sequential fallback attempt blocked at stage={stage}: {reason}";
        RecordSmokeFailureOnce(message);
        Debug.RenderingWarningEvery(
            $"OpenXR.StrictSps.SequentialFallbackBlocked.{stage}.{GetHashCode()}",
            TimeSpan.FromSeconds(1),
            "[OpenXR] {0}",
            message);
    }

    private bool IsStrictSpsFailureInjectionEligible(OpenXrStrictSpsFailureStage stage)
        => _strictSpsInjectedFailureStage == stage &&
           Volatile.Read(ref _strictSpsInjectedFailureCount) == 0L &&
           SmokeCompletedFrameCount >= _strictSpsInjectedFailureWarmupFrameCount &&
           Volatile.Read(ref _strictSpsSuccessfulSubmissionCount) >= _strictSpsInjectedFailureWarmupFrameCount;

    private bool TryCommitStrictSpsFailure(
        OpenXrStrictSpsFailureStage stage,
        string queueDisposition,
        out OpenXrStrictSpsFailureResolution resolution)
    {
        resolution = default;
        if (!IsStrictSpsFailureInjectionEligible(stage) ||
            Interlocked.CompareExchange(ref _strictSpsInjectedFailureCount, 1L, 0L) != 0L)
        {
            return false;
        }

        resolution = OpenXrStrictSpsFailurePolicy.Resolve(stage);
        Volatile.Write(
            ref _strictSpsInjectedFallbackBaseline,
            Volatile.Read(ref _strictSinglePassStereoSequentialFallbackAttemptCount));
        Volatile.Write(ref _strictSpsInjectedFailureHandled, resolution.Handled ? 1 : 0);
        Volatile.Write(ref _strictSpsInjectedProjectionLayerCount, resolution.ProjectionLayerCount);
        Volatile.Write(
            ref _strictSpsInjectedSequentialFallbackRequested,
            resolution.SequentialFallbackRequested ? 1 : 0);
        Volatile.Write(
            ref _strictSpsInjectedCompletedFrameCount,
            checked(SmokeCompletedFrameCount + 1L));
        _strictSpsInjectedQueueDisposition = queueDisposition;
        Debug.RenderingWarning(
            "[OpenXR] Injected strict SinglePassStereo failure at stage={0}; handled={1} layers={2} sequentialFallbackRequested={3} fallbackDelta={4}.",
            stage,
            resolution.Handled,
            resolution.ProjectionLayerCount,
            resolution.SequentialFallbackRequested,
            resolution.SequentialFallbackAttemptDelta);
        return true;
    }

    private void RecordStrictSpsSuccessfulSubmission()
        => Interlocked.Increment(ref _strictSpsSuccessfulSubmissionCount);

    private void RecordSmokeTeardownCompleted()
        => Volatile.Write(ref _smokeTeardownCompleted, 1);

    private void RecordSmokeEffectiveTsrRenderScale(float? scale)
    {
        float value = scale.HasValue && float.IsFinite(scale.Value)
            ? Math.Clamp(scale.Value, 0.5f, 1.0f)
            : float.NaN;
        Volatile.Write(
            ref _smokeEffectiveTsrRenderScaleBits,
            BitConverter.SingleToInt32Bits(value));
    }

    private void RecordSmokeWarning(string warning)
    {
        lock (_smokeDiagnosticsLock)
            _smokeWarnings.Add(warning);
    }

    private void RecordSmokeFailure(string failure)
    {
        lock (_smokeDiagnosticsLock)
            _smokeFailures.Add(failure);
    }

    private void RecordSmokeFailureOnce(string failure)
    {
        lock (_smokeDiagnosticsLock)
        {
            foreach (string existing in _smokeFailures)
            {
                if (string.Equals(existing, failure, StringComparison.Ordinal))
                    return;
            }

            _smokeFailures.Add(failure);
        }
    }

    private static void IncrementEyeCounter(long[] counters, uint viewIndex)
    {
        if (viewIndex >= counters.Length)
            return;

        Interlocked.Increment(ref counters[viewIndex]);
    }

    private static long ReadEyeCounter(long[] counters, uint viewIndex)
        => viewIndex < counters.Length ? Volatile.Read(ref counters[viewIndex]) : 0;

    private static long[] CopyCounterArray(long[] source)
    {
        var copy = new long[source.Length];
        for (int i = 0; i < source.Length; i++)
            copy[i] = Volatile.Read(ref source[i]);
        return copy;
    }

    private static string[] BuildFoveationCapabilityNames(VrFoveationBackendCapabilities capabilities)
    {
        List<string> names = [];
        if (capabilities.VulkanFragmentShadingRate)
            names.Add(nameof(capabilities.VulkanFragmentShadingRate));
        if (capabilities.VulkanFragmentDensityMap)
            names.Add(nameof(capabilities.VulkanFragmentDensityMap));
        if (capabilities.OpenXrRuntimeFoveation)
            names.Add(nameof(capabilities.OpenXrRuntimeFoveation));
        if (capabilities.OpenXrQuadViews)
            names.Add(nameof(capabilities.OpenXrQuadViews));
        if (capabilities.OpenGlFixedFoveationExtension)
            names.Add(nameof(capabilities.OpenGlFixedFoveationExtension));
        if (capabilities.OpenGlMultiResolution)
            names.Add(nameof(capabilities.OpenGlMultiResolution));
        return [.. names];
    }

    private bool HasAnyTrackerPoseAvailable()
    {
        lock (_openXrPoseLock)
            return _openXrPredTrackerLocalPose.Count > 0 || _openXrLateTrackerLocalPose.Count > 0;
    }

    private static (string? RuntimeName, string? RuntimeVersion) TryReadRuntimeManifestMetadata(string? runtimeManifestPath)
    {
        if (string.IsNullOrWhiteSpace(runtimeManifestPath) || !File.Exists(runtimeManifestPath))
            return (null, null);

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(runtimeManifestPath));
            if (!document.RootElement.TryGetProperty("runtime", out var runtime))
                return (null, null);

            string? name = runtime.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()
                : null;
            string? version = runtime.TryGetProperty("api_version", out var versionElement)
                ? versionElement.GetString()
                : null;
            return (name, version);
        }
        catch
        {
            return (null, null);
        }
    }
}
