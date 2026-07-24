using Silk.NET.OpenXR;
using Silk.NET.Windowing;
using System;
using System.IO;
using System.Threading;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;
using Debug = XREngine.Debug;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    internal void EnableRuntimeMonitoring()
    {
        SubscribeOpenXrRenderSettingsChanged();
        RecordAppliedOpenXrEyeResolutionSettings();
        _runtimeMonitoringEnabled = true;
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

    internal void PrepareRendererDeviceTeardown(AbstractRenderer renderer, string reason)
    {
        if (renderer is not VulkanRenderer && renderer is not OpenGLRenderer)
            return;

        if (_session.Handle == 0 &&
            _instance.Handle == 0 &&
            _graphicsBinding is null &&
            !_instanceOwnedByRenderer)
        {
            return;
        }

        Debug.LogWarning($"OpenXR tearing down graphics session before renderer device teardown. Renderer={renderer.GetType().Name} Reason={reason}");
        _sessionBegun = false;
        Volatile.Write(ref _pendingXrFrame, 0);
        Volatile.Write(ref _pendingXrFrameCollected, 0);
        Volatile.Write(ref _framePrepared, 0);
        Volatile.Write(ref _frameSkipRender, 0);

        bool destroyInstance = renderer is VulkanRenderer || _instanceOwnedByRenderer;
        TearDownSessionResourcesOnOwningThread(destroyInstance);

        ScheduleProbeRetry(GetGraphicsDeviceFailureProbeDelay());
        SetRuntimeState(_runtimeMonitoringEnabled ? OpenXrRuntimeState.RecreatePending : OpenXrRuntimeState.DesktopOnly);
    }

    internal void UpdateRuntimeState()
    {
        if (!_runtimeMonitoringEnabled)
            return;

        if (Window is null || Window.Renderer is null)
            return;

        if (_runtimeState == OpenXrRuntimeState.Unavailable)
            return;

        if (Window.Renderer is VulkanRenderer &&
            !RuntimeEngine.IsRenderThread &&
            RequiresVulkanRuntimeStateRenderThread())
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
                TearDownSessionResourcesOnOwningThread(false);
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

    private bool RequiresVulkanRuntimeStateRenderThread()
        => _runtimeState != OpenXrRuntimeState.SessionRunning ||
           Volatile.Read(ref _runtimeLossPending) != 0;

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

        IXrGraphicsBinding? selectedBinding = renderer switch
        {
            VulkanRenderer => new VulkanXrGraphicsBinding(),
            OpenGLRenderer => new OpenGLXrGraphicsBinding(),
            _ => null
        };

        if (selectedBinding is null)
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
            || _graphicsBinding.GetType() != selectedBinding.GetType())
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

        if (renderer is VulkanRenderer sessionStartVulkanRenderer &&
            sessionStartVulkanRenderer.ShouldDeferOpenXrRuntimeSessionStart(out string deferReason))
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.SessionStartDeferred.{sessionStartVulkanRenderer.GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Deferring Vulkan session creation: {0}",
                deferReason);
            ScheduleProbeRetry(TimeSpan.FromMilliseconds(100));
            return;
        }

        if (renderer is OpenGLRenderer)
        {
            var window = Window;
            if (window is null)
                return;

            if (_deferredOpenGlInit is not null)
                return;

            _deferredOpenGlInit = () =>
            {
                if (Window is null || Window.Renderer is not OpenGLRenderer glRenderer)
                    return;

                if (_runtimeState != OpenXrRuntimeState.XrSystemReady)
                    return;

                IXrGraphicsBinding? graphicsBinding = _graphicsBinding;
                if (graphicsBinding is null)
                    return;

                Window.RenderViewportsCallback -= _deferredOpenGlInit;
                _deferredOpenGlInit = null;

                try
                {
                    graphicsBinding.TryCreateSession(this, glRenderer);
                    RecordSmokeSessionCreated(graphicsBinding.BackendName);
                    CreateReferenceSpace();
                    graphicsBinding.CreateSwapchains(this, glRenderer);
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
            if (renderer is VulkanRenderer vulkanRenderer)
            {
                vulkanRenderer.ExecuteOpenXrRuntimeGraphicsTransition(
                    "OpenXR session and swapchain initialization",
                    () =>
                    {
                        graphicsBinding.TryCreateSession(this, renderer);
                        RecordSmokeSessionCreated(graphicsBinding.BackendName);
                        CreateReferenceSpace();
                        graphicsBinding.CreateSwapchains(this, renderer);
                        EnsureInputCreated();
                    });
            }
            else
            {
                graphicsBinding.TryCreateSession(this, renderer);
                RecordSmokeSessionCreated(graphicsBinding.BackendName);
                CreateReferenceSpace();
                graphicsBinding.CreateSwapchains(this, renderer);
                EnsureInputCreated();
            }

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

    private void HandleRuntimeLoss()
    {
        OpenXrRuntimeLossReason lossReason = _runtimeLossReason;
        Debug.LogWarning($"OpenXR runtime loss detected: {lossReason}");

        bool stopMonitoring = lossReason == OpenXrRuntimeLossReason.SessionExiting
            || lossReason == OpenXrRuntimeLossReason.ShutdownRequested;
        bool destroyInstance = stopMonitoring
            || lossReason == OpenXrRuntimeLossReason.InstanceLostError
            || lossReason == OpenXrRuntimeLossReason.RuntimeUnavailable;

        TearDownSessionResourcesOnOwningThread(destroyInstance);
        if (!stopMonitoring)
            TryEnsureOpenXrRuntimeService($"OpenXR runtime loss: {lossReason}");

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

    private void MarkRuntimeLoss(OpenXrRuntimeLossReason reason)
    {
        if (reason == OpenXrRuntimeLossReason.None)
            return;

        lock (_runtimeLossLock)
        {
            if (Volatile.Read(ref _runtimeLossPending) == 0 ||
                GetRuntimeLossReasonSeverity(reason) >= GetRuntimeLossReasonSeverity(_runtimeLossReason))
            {
                _runtimeLossReason = reason;
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
            MarkRuntimeLoss(OpenXrRuntimeLossReason.SessionLostError);
        else if (result == Result.ErrorInstanceLost)
            MarkRuntimeLoss(OpenXrRuntimeLossReason.InstanceLostError);
        else if (result == Result.ErrorRuntimeFailure)
            MarkRuntimeLoss(OpenXrRuntimeLossReason.RuntimeUnavailable);

        if (result != Result.Success)
            RecordSmokeFailure($"{operation} returned {result}.");

        return result;
    }

    private void TearDownSessionResourcesOnOwningThread(bool destroyInstance)
    {
        if (Window?.Renderer is VulkanRenderer && !RuntimeEngine.IsRenderThread)
        {
            RuntimeRenderingHostServices.Scheduling.InvokeRenderThreadTask(
                () =>
                {
                    TearDownSessionResourcesWithCurrentContext(destroyInstance);
                    return true;
                },
                "OpenXR.Vulkan.TeardownSessionResources",
                RenderThreadJobKind.RequiresGraphicsContext);
            return;
        }

        if (_gl is not null && !RuntimeEngine.IsRenderThread)
        {
            RuntimeRenderingHostServices.Scheduling.InvokeRenderThreadTask(
                () =>
                {
                    TearDownSessionResourcesWithCurrentContext(destroyInstance);
                    return true;
                },
                "OpenXR.OpenGL.TeardownSessionResources",
                RenderThreadJobKind.RequiresGraphicsContext);
            return;
        }

        TearDownSessionResourcesWithCurrentContext(destroyInstance);
    }

    private void TearDownSessionResourcesWithCurrentContext(bool destroyInstance)
    {
        if (_gl is not null && Window is not null && wglGetCurrentContext() == 0)
        {
            try
            {
                Window.Window.MakeCurrent();
            }
            catch (Exception ex)
            {
                RecordSmokeWarning($"OpenXR OpenGL teardown could not make the window context current: {ex.Message}");
                Debug.LogWarning($"OpenXR OpenGL teardown could not make the window context current: {ex.Message}");
            }
        }

        TearDownSessionResources(destroyInstance);
    }

    private void TearDownSessionResources(bool destroyInstance)
    {
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
                _graphicsBinding.WaitForGpuIdle(this, renderer);
            }
            catch
            {
                // Best-effort idle wait.
            }
        }

        CleanupSwapchains();

        DestroyInput();

        if (_appSpace.Handle != 0)
        {
            Api.DestroySpace(_appSpace);
            _appSpace = default;
        }

        if (_session.Handle != 0)
        {
            Api.DestroySession(_session);
            _session = default;
        }

        if (destroyInstance && _instance.Handle != 0)
        {
            DestroyValidationLayers();
            DestroyInstance();
            _instance = default;
            _systemId = 0;
            _win32PerformanceCounterTimeExtension = null;
            Volatile.Write(ref _win32PerformanceCounterTimeExtensionChecked, 0);
        }

        RecordSmokeTeardownCompleted();
    }
}
