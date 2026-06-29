using Silk.NET.OpenXR;
using Silk.NET.Windowing;
using System;
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
        _runtimeMonitoringEnabled = true;
        ResetSmokeDiagnostics();
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

        ScheduleProbeRetry(_graphicsDeviceFailureProbeInterval);
        SetRuntimeState(_runtimeMonitoringEnabled ? OpenXrRuntimeState.RecreatePending : OpenXrRuntimeState.DesktopOnly);
    }

    internal void UpdateRuntimeState()
    {
        if (!_runtimeMonitoringEnabled)
            return;

        if (Window is null || Window.Renderer is null)
            return;

        if (Window.Renderer is VulkanRenderer &&
            !RuntimeEngine.IsRenderThread &&
            RequiresVulkanRuntimeStateRenderThread())
        {
            RuntimeRenderingHostServices.Current.InvokeRenderThreadTask(
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
            CreateInstance();
            SetupDebugMessenger();
            SetRuntimeState(OpenXrRuntimeState.XrInstanceReady);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"OpenXR probe: instance creation failed. {ex.Message}");
            ScheduleProbeRetry();
        }
    }

    private void TryCreateSystem()
    {
        try
        {
            CreateSystem();
            SetRuntimeState(OpenXrRuntimeState.XrSystemReady);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"OpenXR probe: system creation failed. {ex.Message}");
            ScheduleProbeRetry();
            TearDownSessionResourcesOnOwningThread(true);
            SetRuntimeState(OpenXrRuntimeState.RecreatePending);
        }
    }

    private void TryCreateSessionAndSwapchains(AbstractRenderer renderer)
    {
        TryEnsureOpenXrRuntimeService("OpenXR session creation");

        if (renderer.IsDeviceLost)
        {
            Debug.LogWarning("OpenXR session init skipped because the active renderer device is lost.");
            ScheduleProbeRetry(_graphicsDeviceFailureProbeInterval);
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
            ScheduleProbeRetry(_graphicsDeviceFailureProbeInterval);
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
            ScheduleProbeRetry(_graphicsDeviceFailureProbeInterval);
            TearDownSessionResourcesOnOwningThread(true);
            SetRuntimeState(OpenXrRuntimeState.RecreatePending);
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
            graphicsBinding.TryCreateSession(this, renderer);
            RecordSmokeSessionCreated(graphicsBinding.BackendName);
            CreateReferenceSpace();
            graphicsBinding.CreateSwapchains(this, renderer);
            EnsureInputCreated();
            SetRuntimeState(OpenXrRuntimeState.SessionCreated);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"OpenXR session init failed: {ex.Message}");
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
            ? _graphicsDeviceFailureProbeInterval
            : _probeInterval;

    private void ScheduleProbeRetry()
        => ScheduleProbeRetry(_probeInterval);

    private void ScheduleProbeRetry(TimeSpan delay)
        => _nextProbeUtc = DateTime.UtcNow + delay;

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
            if (RuntimeRenderingHostServices.Current.TryEnsureOpenXrRuntimeService(reason))
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
            RuntimeRenderingHostServices.Current.InvokeRenderThreadTask(
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
            RuntimeRenderingHostServices.Current.InvokeRenderThreadTask(
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
