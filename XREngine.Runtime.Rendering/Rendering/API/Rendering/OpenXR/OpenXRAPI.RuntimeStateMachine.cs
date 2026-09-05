using Silk.NET.OpenXR;
using Silk.NET.Windowing;
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using XREngine.Rendering;
using Debug = XREngine.Debug;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    internal void EnableRuntimeMonitoring()
    {
        InvalidateOpenXrViewHistory();
        SubscribeOpenXrRenderSettingsChanged();
        RecordAppliedOpenXrEyeResolutionSettings();
        _runtimeMonitoringEnabled = true;
        if (!_pendingShutdownCleanup)
            _graphicsBackendResourcesDestroyed = false;
        ResetSmokeDiagnostics();
        ResetOpenXrProbeFailureState();
        SetRuntimeState(OpenXrRuntimeState.DesktopOnly);
        _runtimeLossReason = OpenXrRuntimeLossReason.None;
        Interlocked.Exchange(ref _runtimeLossPending, 0);
        _sessionBegun = false;
        _sessionState = SessionState.Unknown;
        Volatile.Write(ref _pendingXrFrame, 0);
        Volatile.Write(ref _pendingXrFrameCollected, 0);
        Volatile.Write(ref _framePrepared, 0);
        Volatile.Write(ref _frameSkipRender, 0);
        Volatile.Write(ref _hasLastValidViews, 0);
        StopOpenXrPacingThread();
        ClearOpenXrCollectVisiblePrepThread();
        _openXrActionsSyncedFrameNumber = 0;
        _nextProbeUtc = DateTime.UtcNow;
    }

    internal void DisableRuntimeMonitoring()
    {
        _runtimeMonitoringEnabled = false;
        UnsubscribeOpenXrRenderSettingsChanged();
        MarkRuntimeLoss(OpenXrRuntimeLossReason.ShutdownRequested);
    }

    internal bool PrepareRendererDeviceTeardown(AbstractRenderer renderer, string reason)
    {
        if (!TryGetOrCreateGraphicsBinding(renderer, out IXrGraphicsBinding? binding))
            return true;

        if (_session.Handle == 0 &&
            _instance.Handle == 0 &&
            _graphicsBinding is null &&
            !_instanceOwnedByRenderer)
        {
            return true;
        }

        Debug.LogWarning($"OpenXR tearing down graphics session before renderer device teardown. Renderer={renderer.GetType().Name} Reason={reason}");
        _sessionBegun = false;
        Volatile.Write(ref _pendingXrFrame, 0);
        Volatile.Write(ref _pendingXrFrameCollected, 0);
        Volatile.Write(ref _framePrepared, 0);
        Volatile.Write(ref _frameSkipRender, 0);

        bool destroyInstance = binding.DestroysRuntimeInstanceOnRendererTeardown || _instanceOwnedByRenderer;
        if (!TearDownSessionResourcesOnOwningThread(destroyInstance))
        {
            ScheduleProbeRetry(TimeSpan.FromMilliseconds(100));
            SetRuntimeState(OpenXrRuntimeState.RecreatePending);
            return false;
        }

        ScheduleProbeRetry(GetGraphicsDeviceFailureProbeDelay());
        SetRuntimeState(_runtimeMonitoringEnabled ? OpenXrRuntimeState.RecreatePending : OpenXrRuntimeState.DesktopOnly);
        return true;
    }
    internal void UpdateRuntimeState()
    {
        if (_pendingShutdownCleanup)
        {
            if (!RuntimeEngine.IsRenderThread &&
                Window?.Renderer is AbstractRenderer &&
                _graphicsBinding is not null &&
                _graphicsBinding.RequiresRenderThreadForTeardown)
            {
                RuntimeRenderingHostServices.Scheduling.InvokeRenderThreadTask(
                    () =>
                    {
                        UpdateRuntimeState();
                        return true;
                    },
                    "OpenXR.PendingShutdownCleanup",
                    RenderThreadJobKind.RequiresGraphicsContext);
                return;
            }

            ServicePendingShutdownCleanup();
            return;
        }

        if (!_runtimeMonitoringEnabled)
            return;

        if (Window is null || Window.Renderer is null)
            return;

        if (_runtimeState == OpenXrRuntimeState.Unavailable)
            return;

        AbstractRenderer renderer = Window.Renderer;
        if (!RuntimeEngine.IsRenderThread &&
            TryGetOrCreateGraphicsBinding(renderer, out IXrGraphicsBinding? binding) &&
            binding.RequiresRuntimeStateRenderThread(
                _runtimeState,
                Volatile.Read(ref _runtimeLossPending) != 0))
        {
            RuntimeRenderingHostServices.Scheduling.InvokeRenderThreadTask(
                () =>
                {
                    UpdateRuntimeState();
                    return true;
                },
                "OpenXR.Vulkan.UpdateRuntimeState",
                RenderThreadJobKind.RequiresGraphicsContext);
            return;
        }

        if (_instance.Handle != 0 && _runtimeState != OpenXrRuntimeState.SessionRunning)
            PollEvents();

        if (_graphicsBinding?.HasPendingDeferredSwapchainRetirement == true)
            _graphicsBinding.PollDeferredSwapchainRetirement(this, renderer);

        if (ConsumeRuntimeLoss(out var lossReason))
        {
            _runtimeLossReason = lossReason;
            SetRuntimeState(OpenXrRuntimeState.SessionLost);
        }

        switch (_runtimeState)
        {
            case OpenXrRuntimeState.DesktopOnly:
                TryProbeRuntime();
                break;
            case OpenXrRuntimeState.XrInstanceReady:
                TryCreateSystem();
                break;
            case OpenXrRuntimeState.XrSystemReady:
                TryCreateSessionAndSwapchains(Window.Renderer);
                break;
            case OpenXrRuntimeState.SessionCreated:
                if (IsSessionRunningState(_sessionState) && _sessionBegun)
                    SetRuntimeState(OpenXrRuntimeState.SessionRunning);
                break;
            case OpenXrRuntimeState.SessionRunning:
                OpenXrEyeResolutionSettingsSnapshot currentResolution = CaptureCurrentOpenXrEyeResolutionSettings();
                OpenXrEyeResolutionSettingsSnapshot appliedResolution = CaptureAppliedOpenXrEyeResolutionSettings();
                if (!OpenXrEyeResolutionSettingsMatch(currentResolution, appliedResolution))
                    QueueOpenXrEyeResolutionSessionRecreate(currentResolution, appliedResolution);

                if (_sessionState == SessionState.Stopping
                    || _sessionState == SessionState.Exiting
                    || _sessionState == SessionState.LossPending)
                {
                    SetRuntimeState(OpenXrRuntimeState.SessionStopping);
                }
                else if (_sessionBegun && !IsSessionRunningState(_sessionState))
                    SetRuntimeState(OpenXrRuntimeState.SessionStopping);
                break;
            case OpenXrRuntimeState.SessionStopping:
                if (TearDownSessionResourcesOnOwningThread(false))
                    SetRuntimeState(OpenXrRuntimeState.DesktopOnly);
                break;
            case OpenXrRuntimeState.SessionLost:
                HandleRuntimeLoss();
                break;
            case OpenXrRuntimeState.RecreatePending:
                if (DateTime.UtcNow >= _nextProbeUtc)
                    SetRuntimeState(OpenXrRuntimeState.DesktopOnly);
                break;
        }
    }

    private void ServicePendingShutdownCleanup()
    {
        if (DateTime.UtcNow < _nextProbeUtc)
            return;

        if (Window?.Renderer is AbstractRenderer renderer && _graphicsBinding is not null)
            _graphicsBinding.PollDeferredSwapchainRetirement(this, renderer);
        if (!TearDownSessionResourcesOnOwningThread(true))
        {
            ScheduleProbeRetry(TimeSpan.FromMilliseconds(100));
            return;
        }

        CompleteGraphicsBackendCleanup();
    }

    private void TryProbeRuntime()
    {
        if (DateTime.UtcNow < _nextProbeUtc)
            return;

        TryEnsureOpenXrRuntimeService("OpenXR runtime probe");

        if (_instance.Handle != 0)
        {
            SetRuntimeState(OpenXrRuntimeState.XrInstanceReady);
            return;
        }

        try
        {
            OpenXrInstanceCreationAttempt attempt = TryCreateInstance();
            if (!attempt.Succeeded)
            {
                HandleInstanceProbeFailure(attempt);
                return;
            }

            _consecutiveInstanceProbeFailures = 0;
            _runtimeFailureReason = null;
            SetupDebugMessenger();
            SetRuntimeState(OpenXrRuntimeState.XrInstanceReady);
        }
        catch (Exception ex)
        {
            HandleUnexpectedInstanceProbeFailure(ex);
        }
    }

    private void TryCreateSystem()
    {
        if (DateTime.UtcNow < _nextProbeUtc)
            return;

        Result result;
        try
        {
            result = CreateSystem();
        }
        catch (Exception ex)
        {
            HandleUnexpectedSystemProbeFailure(ex);
            return;
        }

        if (result == Result.Success)
        {
            _consecutiveSystemProbeFailures = 0;
            _runtimeFailureReason = null;
            SetRuntimeState(OpenXrRuntimeState.XrSystemReady);
            return;
        }

        HandleSystemProbeFailure(result);
    }

    private void HandleInstanceProbeFailure(OpenXrInstanceCreationAttempt attempt)
    {
        int failureCount = ++_consecutiveInstanceProbeFailures;
        OpenXrProbeRetryDecision decision = OpenXrProbeRetryPolicy.ForCreateInstanceResult(
            attempt.Result,
            failureCount,
            _probeInterval,
            _maximumProbeRetryInterval);
        _runtimeFailureReason =
            $"Stage={attempt.Operation}; Result={attempt.Result}; Category={decision.Category}; Reason={attempt.FailureReason}";
        RecordSmokeFailureOnce($"OpenXR instance probe failed. {_runtimeFailureReason}");

        if (decision.ShouldRetry)
        {
            Debug.VR(
                "[WARN] OpenXR instance probe failed; retry scheduled with exponential backoff. " +
                $"Stage={attempt.Operation}; Result={attempt.Result}; Category={decision.Category}; Attempt={failureCount}; " +
                $"RetryIn={decision.Delay.TotalSeconds:0.###}s; Reason={attempt.FailureReason}");
            ScheduleProbeRetry(decision.Delay);
            SetRuntimeState(OpenXrRuntimeState.RecreatePending);
            return;
        }

        Debug.VR(
            "[ERROR] OpenXR instance probe failed with a non-recoverable configuration or capability error. " +
            $"Stage={attempt.Operation}; Result={attempt.Result}; Category={decision.Category}; Reason={attempt.FailureReason}; " +
            "automatic probing is halted until OpenXR is reconfigured or runtime monitoring is restarted.");
        SetRuntimeState(OpenXrRuntimeState.Unavailable);
    }

    private void HandleUnexpectedInstanceProbeFailure(Exception ex)
    {
        int failureCount = ++_consecutiveInstanceProbeFailures;
        bool configurationFailure = ex is DllNotFoundException
            or FileNotFoundException
            or BadImageFormatException
            or EntryPointNotFoundException;
        _runtimeFailureReason =
            $"Stage=xrCreateInstance; ManagedException={ex.GetType().FullName}; Reason={ex.Message}";
        RecordSmokeFailureOnce($"OpenXR instance probe failed unexpectedly. {_runtimeFailureReason}");

        if (configurationFailure)
        {
            Debug.VR(
                "[ERROR] OpenXR instance probe failed before the runtime returned an OpenXR Result. " +
                $"Exception={ex.GetType().FullName}; Reason={ex.Message}; " +
                "automatic probing is halted until OpenXR is reconfigured or runtime monitoring is restarted.");
            if (_instance.Handle != 0)
                TearDownSessionResourcesOnOwningThread(true);
            SetRuntimeState(OpenXrRuntimeState.Unavailable);
            return;
        }

        TimeSpan delay = OpenXrProbeRetryPolicy.CalculateBackoff(
            failureCount,
            _probeInterval,
            _maximumProbeRetryInterval);
        Debug.VR(
            "[WARN] OpenXR instance probe raised an unexpected managed exception; retry scheduled with exponential backoff. " +
            $"Exception={ex.GetType().FullName}; Attempt={failureCount}; RetryIn={delay.TotalSeconds:0.###}s; Reason={ex.Message}");
        if (_instance.Handle != 0)
            TearDownSessionResourcesOnOwningThread(true);
        ScheduleProbeRetry(delay);
        SetRuntimeState(OpenXrRuntimeState.RecreatePending);
    }

    private void HandleSystemProbeFailure(Result result)
    {
        int failureCount = ++_consecutiveSystemProbeFailures;
        OpenXrProbeRetryDecision decision = OpenXrProbeRetryPolicy.ForGetSystemResult(
            result,
            failureCount,
            _probeInterval,
            _maximumProbeRetryInterval);
        _runtimeFailureReason =
            $"Stage=xrGetSystem; Result={result}; Category={decision.Category}; FormFactor={FormFactor.HeadMountedDisplay}";
        RecordSmokeFailureOnce($"OpenXR system probe failed. {_runtimeFailureReason}");

        if (decision.ShouldRetry)
        {
            Debug.VR(
                "[WARN] OpenXR system probe did not find a usable HMD; retry scheduled with exponential backoff. " +
                $"Result={result}; Category={decision.Category}; Attempt={failureCount}; RetryIn={decision.Delay.TotalSeconds:0.###}s.");
            ScheduleProbeRetry(decision.Delay);

            if (!decision.RecreateInstance)
            {
                // The instance remains valid. Retrying xrGetSystem avoids repeatedly recreating the
                // instance and re-running extension negotiation while a headset is disconnected.
                return;
            }

            TearDownSessionResourcesOnOwningThread(true);
            SetRuntimeState(OpenXrRuntimeState.RecreatePending);
            return;
        }

        Debug.VR(
            "[ERROR] OpenXR system probe failed with a non-recoverable error. " +
            $"Result={result}; Category={decision.Category}; " +
            "automatic probing is halted until OpenXR is reconfigured or runtime monitoring is restarted.");
        TearDownSessionResourcesOnOwningThread(true);
        SetRuntimeState(OpenXrRuntimeState.Unavailable);
    }

    private void HandleUnexpectedSystemProbeFailure(Exception ex)
    {
        int failureCount = ++_consecutiveSystemProbeFailures;
        TimeSpan delay = OpenXrProbeRetryPolicy.CalculateBackoff(
            failureCount,
            _probeInterval,
            _maximumProbeRetryInterval);
        _runtimeFailureReason =
            $"Stage=xrGetSystem; ManagedException={ex.GetType().FullName}; Reason={ex.Message}";
        RecordSmokeFailureOnce($"OpenXR system probe failed unexpectedly. {_runtimeFailureReason}");
        Debug.VR(
            "[WARN] OpenXR system probe raised an unexpected managed exception; the instance will be recreated after backoff. " +
            $"Exception={ex.GetType().FullName}; Attempt={failureCount}; RetryIn={delay.TotalSeconds:0.###}s; Reason={ex.Message}");
        TearDownSessionResourcesOnOwningThread(true);
        ScheduleProbeRetry(delay);
        SetRuntimeState(OpenXrRuntimeState.RecreatePending);
    }

    private void TryCreateSessionAndSwapchains(AbstractRenderer renderer)
    {
        if (DateTime.UtcNow < _nextProbeUtc)
            return;

        if (_session.Handle != 0 || HasCreatedOpenXrSwapchains())
        {
            Debug.RenderingWarningEvery(
                "OpenXR.SessionCreationDeferred.PendingTeardown",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Deferring new session creation until the previous session and its swapchain retirement complete.");
            if (TearDownSessionResourcesOnOwningThread(destroyInstance: true))
            {
                ScheduleProbeRetry(TimeSpan.FromMilliseconds(100));
                SetRuntimeState(OpenXrRuntimeState.RecreatePending);
                return;
            }

            ScheduleProbeRetry(TimeSpan.FromMilliseconds(100));
            SetRuntimeState(OpenXrRuntimeState.RecreatePending);
            return;
        }

        TryEnsureOpenXrRuntimeService("OpenXR session creation");

        if (renderer.IsDeviceLost)
        {
            Debug.LogWarning("OpenXR session init skipped because the active renderer device is lost.");
            RecordSmokeFailureOnce(
                $"OpenXR session init skipped because the active renderer device is lost. Renderer={renderer.GetType().FullName}; Reason={renderer.DeviceLostReason ?? "<unknown>"}");
            ScheduleProbeRetry(GetGraphicsDeviceFailureProbeDelay());
            TearDownSessionResourcesOnOwningThread(true);
            SetRuntimeState(OpenXrRuntimeState.RecreatePending);
            return;
        }

        if (!OpenXrGraphicsBindingRegistry.TryCreate(renderer, out IXrGraphicsBinding? selectedBinding))
        {
            Debug.LogWarning("OpenXR: no compatible graphics binding for the active renderer.");
            RecordSmokeFailureOnce($"OpenXR session init skipped because renderer '{renderer.GetType().FullName}' has no compatible graphics binding.");
            ScheduleProbeRetry(GetGraphicsDeviceFailureProbeDelay());
            TearDownSessionResourcesOnOwningThread(true);
            SetRuntimeState(OpenXrRuntimeState.RecreatePending);
            return;
        }

        if (_graphicsBinding is null
            || !_graphicsBinding.IsCompatible(renderer)
            || _graphicsBinding.BackendId != selectedBinding.BackendId)
        {
            _graphicsBinding = selectedBinding;
        }

        if (_graphicsBinding is null || !_graphicsBinding.IsCompatible(renderer))
        {
            Debug.LogWarning("OpenXR: no compatible graphics binding for the active renderer.");
            RecordSmokeFailureOnce($"OpenXR session init skipped because graphics binding '{_graphicsBinding?.GetType().FullName ?? "<null>"}' is not compatible with renderer '{renderer.GetType().FullName}'.");
            ScheduleProbeRetry(GetGraphicsDeviceFailureProbeDelay());
            TearDownSessionResourcesOnOwningThread(true);
            SetRuntimeState(OpenXrRuntimeState.RecreatePending);
            return;
        }

        if (_graphicsBinding.ShouldDeferSessionStart(renderer, out string deferReason))
        {
            Debug.RenderingWarningEvery(
                $"OpenXR.SessionStartDeferred.{renderer.BackendId}.{renderer.GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Deferring {0} session creation: {1}",
                _graphicsBinding.BackendName,
                deferReason);
            ScheduleProbeRetry(TimeSpan.FromMilliseconds(100));
            return;
        }

        if (_graphicsBinding.RequiresDeferredSessionCreation)
        {
            var window = Window;
            if (window is null)
                return;

            if (_deferredOpenGlInit is not null)
                return;

            IXrGraphicsBinding deferredBinding = _graphicsBinding;
            _deferredOpenGlInit = () =>
            {
                if (Window?.Renderer is not AbstractRenderer activeRenderer ||
                    !deferredBinding.IsCompatible(activeRenderer))
                    return;

                if (_runtimeState != OpenXrRuntimeState.XrSystemReady)
                    return;

                IXrGraphicsBinding? graphicsBinding = _graphicsBinding;
                if (graphicsBinding is null || graphicsBinding.BackendId != deferredBinding.BackendId)
                    return;

                Window.RenderViewportsCallback -= _deferredOpenGlInit;
                _deferredOpenGlInit = null;

                try
                {
                    graphicsBinding.TryCreateSession(this, activeRenderer);
                    RecordSmokeSessionCreated(graphicsBinding.BackendName);
                    CreateReferenceSpace();
                    graphicsBinding.CreateSwapchains(this, activeRenderer);
                    RecordAppliedOpenXrEyeResolutionSettings();
                    EnsureInputCreated();
                    SetRuntimeState(OpenXrRuntimeState.SessionCreated);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"OpenXR OpenGL session init failed: {ex.Message}");
                    RecordSmokeFailureOnce($"OpenXR OpenGL session init failed: {ex.GetType().Name}: {ex.Message}");
                    ScheduleProbeRetry(GetSessionFailureRetryDelay(ex));
                    TearDownSessionResourcesOnOwningThread(true);
                    SetRuntimeState(OpenXrRuntimeState.RecreatePending);
                }
            };

            window.RenderViewportsCallback += _deferredOpenGlInit;
            return;
        }

        IXrGraphicsBinding? graphicsBinding = _graphicsBinding;
        if (graphicsBinding is null)
            return;

        try
        {
            graphicsBinding.ExecuteRuntimeGraphicsTransition(
                renderer,
                "OpenXR session and swapchain initialization",
                () =>
                {
                    graphicsBinding.TryCreateSession(this, renderer);
                    RecordSmokeSessionCreated(graphicsBinding.BackendName);
                    CreateReferenceSpace();
                    graphicsBinding.CreateSwapchains(this, renderer);
                    RecordAppliedOpenXrEyeResolutionSettings();
                    EnsureInputCreated();
                });

            SetRuntimeState(OpenXrRuntimeState.SessionCreated);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"OpenXR session init failed: {ex.Message}");
            RecordSmokeFailureOnce($"OpenXR session init failed: {ex.GetType().Name}: {ex.Message}");
            ScheduleProbeRetry(GetSessionFailureRetryDelay(ex));
            TearDownSessionResourcesOnOwningThread(true);
            SetRuntimeState(OpenXrRuntimeState.RecreatePending);
        }
    }

    private bool TryGetOrCreateGraphicsBinding(
        AbstractRenderer renderer,
        [NotNullWhen(true)] out IXrGraphicsBinding? binding)
    {
        binding = _graphicsBinding;
        if (binding is not null &&
            binding.BackendId == renderer.BackendId &&
            binding.IsCompatible(renderer))
        {
            return true;
        }

        if (!OpenXrGraphicsBindingRegistry.TryCreate(renderer, out binding))
            return false;

        _graphicsBinding = binding;
        return true;
    }

    private void HandleRuntimeLoss()
    {
        OpenXrRuntimeLossReason lossReason = _runtimeLossReason;
        Debug.LogWarning($"OpenXR runtime loss detected: {lossReason}");

        bool stopMonitoring = lossReason == OpenXrRuntimeLossReason.SessionExiting
            || lossReason == OpenXrRuntimeLossReason.ShutdownRequested;
        bool destroyInstance = stopMonitoring
            || lossReason == OpenXrRuntimeLossReason.InstanceLostError
            || lossReason == OpenXrRuntimeLossReason.RuntimeUnavailable;

        bool teardownCompleted = TearDownSessionResourcesOnOwningThread(destroyInstance);
        if (!stopMonitoring && teardownCompleted)
            TryEnsureOpenXrRuntimeService($"OpenXR runtime loss: {lossReason}");

        if (!teardownCompleted)
        {
            ScheduleProbeRetry(TimeSpan.FromMilliseconds(100));
            SetRuntimeState(OpenXrRuntimeState.RecreatePending);
            return;
        }

        if (stopMonitoring)
        {
            _runtimeMonitoringEnabled = false;
            SetRuntimeState(OpenXrRuntimeState.DesktopOnly);
            _runtimeLossReason = OpenXrRuntimeLossReason.None;
            return;
        }

        ScheduleProbeRetry();
        SetRuntimeState(destroyInstance ? OpenXrRuntimeState.RecreatePending : OpenXrRuntimeState.DesktopOnly);
        _runtimeLossReason = OpenXrRuntimeLossReason.None;
    }

    private TimeSpan GetSessionFailureRetryDelay(Exception ex)
        => ex is OpenXrGraphicsSessionException
            {
                Result: Result.ErrorGraphicsDeviceInvalid
                    or Result.ErrorValidationFailure
            }
            ? GetGraphicsDeviceFailureProbeDelay()
            : _probeInterval;

    private TimeSpan GetGraphicsDeviceFailureProbeDelay()
        => DateTime.UtcNow <= _intentionalOpenXrRecreateBackoffBypassUntilUtc
            ? _intentionalOpenXrRecreateProbeInterval
            : _graphicsDeviceFailureProbeInterval;

    private void ScheduleProbeRetry()
        => ScheduleProbeRetry(_probeInterval);

    private void ScheduleProbeRetry(TimeSpan delay)
        => _nextProbeUtc = DateTime.UtcNow + delay;

    private void ResetOpenXrProbeFailureState()
    {
        _consecutiveInstanceProbeFailures = 0;
        _consecutiveSystemProbeFailures = 0;
        _runtimeFailureReason = null;
    }

    private void SetRuntimeState(OpenXrRuntimeState next)
    {
        if (_runtimeState == next)
            return;

        _runtimeState = next;
        Volatile.Write(ref _sessionRunning, next == OpenXrRuntimeState.SessionRunning ? 1 : 0);
        RecordSmokeRuntimeState(next);
    }

    private static bool IsSessionRunningState(SessionState state)
        => state == SessionState.Ready
        || state == SessionState.Synchronized
        || state == SessionState.Visible
        || state == SessionState.Focused;

    private void MarkRuntimeLoss(
        OpenXrRuntimeLossReason reason,
        string operation = "OpenXR runtime state",
        Result? result = null)
    {
        if (reason == OpenXrRuntimeLossReason.None)
            return;

        lock (_runtimeLossLock)
        {
            // One incident has one authoritative observer. Later fallout may be
            // more severe, but it must not replace the call/result that first
            // established the loss transition.
            if (Volatile.Read(ref _runtimeLossPending) == 0)
            {
                _runtimeLossReason = reason;
                _lastRuntimeLossRecord = new OpenXrRuntimeLossRecord(
                    reason,
                    operation,
                    result,
                    DateTimeOffset.UtcNow);
            }

            Volatile.Write(ref _runtimeLossPending, 1);
        }

        ResetOpenXrFrameStateForRuntimeLoss();
    }

    private bool IsOpenXrRuntimeLossPending()
        => Volatile.Read(ref _runtimeLossPending) != 0
        || _runtimeState == OpenXrRuntimeState.SessionLost;

    private void ResetOpenXrFrameStateForRuntimeLoss()
    {
        InvalidateOpenXrViewHistory();
        _sessionBegun = false;
        Volatile.Write(ref _pendingXrFrame, 0);
        Volatile.Write(ref _pendingXrFrameCollected, 0);
        Volatile.Write(ref _pendingXrFrameUsesTrueSinglePassStereo, 0);
        Volatile.Write(ref _framePrepared, 0);
        Volatile.Write(ref _frameSkipRender, 0);

        _openXrPacingWakeEvent.Set();
    }

    private bool ConsumeRuntimeLoss(out OpenXrRuntimeLossReason reason)
    {
        lock (_runtimeLossLock)
        {
            if (Volatile.Read(ref _runtimeLossPending) == 0)
            {
                reason = OpenXrRuntimeLossReason.None;
                return false;
            }

            Volatile.Write(ref _runtimeLossPending, 0);
            reason = _runtimeLossReason;
            _runtimeLossReason = OpenXrRuntimeLossReason.None;
            return true;
        }
    }

    private static int GetRuntimeLossReasonSeverity(OpenXrRuntimeLossReason reason)
        => reason switch
        {
            OpenXrRuntimeLossReason.ShutdownRequested => 100,
            OpenXrRuntimeLossReason.SessionExiting => 90,
            OpenXrRuntimeLossReason.InstanceLostError => 80,
            OpenXrRuntimeLossReason.RuntimeUnavailable => 80,
            OpenXrRuntimeLossReason.SessionLossPending => 70,
            OpenXrRuntimeLossReason.SessionLostError => 60,
            _ => 0,
        };

    private static void TryEnsureOpenXrRuntimeService(string reason)
    {
        try
        {
            if (RuntimeRenderingHostServices.Presentation.TryEnsureOpenXrRuntimeService(reason))
                Debug.Out($"OpenXR runtime service ensured. Reason={reason}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"OpenXR runtime service recovery failed. Reason={reason}; Error={ex.Message}");
        }
    }

    private Result CheckResult(Result result, string operation)
    {
        if (result == Result.ErrorSessionLost)
            MarkRuntimeLoss(OpenXrRuntimeLossReason.SessionLostError, operation, result);
        else if (result == Result.ErrorInstanceLost)
            MarkRuntimeLoss(OpenXrRuntimeLossReason.InstanceLostError, operation, result);
        else if (result == Result.ErrorRuntimeFailure)
            MarkRuntimeLoss(OpenXrRuntimeLossReason.RuntimeUnavailable, operation, result);

        if (result != Result.Success)
            RecordSmokeFailure($"{operation} returned {result}.");

        return result;
    }

    private bool TearDownSessionResourcesOnOwningThread(bool destroyInstance)
    {
        _pendingDestroyInstance |= destroyInstance;
        if (Window?.Renderer is AbstractRenderer renderer &&
            !RuntimeEngine.IsRenderThread &&
            TryGetOrCreateGraphicsBinding(renderer, out IXrGraphicsBinding? binding) &&
            binding.RequiresRenderThreadForTeardown)
        {
            return RuntimeRenderingHostServices.Scheduling.InvokeRenderThreadTask(
                () =>
                {
                    return TearDownSessionResourcesWithCurrentContext(destroyInstance);
                },
                $"OpenXR.{binding.BackendName}.TeardownSessionResources",
                RenderThreadJobKind.RequiresGraphicsContext);
        }

        return TearDownSessionResourcesWithCurrentContext(destroyInstance);
    }

    private bool TearDownSessionResourcesWithCurrentContext(bool destroyInstance)
        => TearDownSessionResources(destroyInstance);

    private bool TearDownSessionResources(bool destroyInstance)
    {
        destroyInstance |= _pendingDestroyInstance;
        if (_deferredOpenGlInit is not null && Window is not null)
        {
            Window.RenderViewportsCallback -= _deferredOpenGlInit;
            _deferredOpenGlInit = null;
        }

        _sessionBegun = false;
        _sessionState = SessionState.Unknown;
        StopOpenXrPacingThread();
        ClearOpenXrCollectVisiblePrepThread();

        if (Window?.Renderer is AbstractRenderer renderer && _graphicsBinding is not null)
        {
            try
            {
                if (!_graphicsBinding.WaitForGpuIdle(this, renderer))
                {
                    Debug.LogWarning("[OpenXR] GPU quiescence is incomplete; retaining OpenXR parents for a later teardown retry.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[OpenXR] GPU quiescence failed before session teardown: {ex.Message}");
                return false;
            }
        }

        if (!CleanupSwapchains() ||
            _graphicsBinding?.HasPendingDeferredSwapchainRetirement == true)
        {
            // Vulkan has retained child swapchain generations whose exact GPU
            // completion is still pending. Destroying this parent session or
            // instance would invalidate those children, so leave the runtime
            // intact and make the deferred teardown decision explicit.
            Debug.LogWarning("[OpenXR] Deferred runtime teardown because Vulkan swapchain retirement is still pending.");
            return false;
        }

        DestroyInput();

        if (_appSpace.Handle != 0)
        {
            if (CheckResult(Api.DestroySpace(_appSpace), "xrDestroySpace") != Result.Success)
                return false;
            _appSpace = default;
        }

        if (_session.Handle != 0)
        {
            if (CheckResult(Api.DestroySession(_session), "xrDestroySession") != Result.Success)
                return false;
            _session = default;
        }

        if (destroyInstance && _instance.Handle != 0)
        {
            DestroyValidationLayers();
            if (!DestroyInstance())
                return false;
            _instance = default;
            _systemId = 0;
            _win32PerformanceCounterTimeExtension = null;
            Volatile.Write(ref _win32PerformanceCounterTimeExtensionChecked, 0);
        }

        if (destroyInstance)
            _pendingDestroyInstance = false;

        RecordSmokeTeardownCompleted();
        return true;
    }
}
