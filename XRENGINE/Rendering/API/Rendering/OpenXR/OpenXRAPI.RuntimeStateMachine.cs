using Silk.NET.OpenXR;
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
        SetRuntimeState(OpenXrRuntimeState.DesktopOnly);
        _runtimeLossReason = OpenXrRuntimeLossReason.None;
        Interlocked.Exchange(ref _runtimeLossPending, 0);
        _sessionBegun = false;
        _sessionState = SessionState.Unknown;
        _nextProbeUtc = DateTime.UtcNow;
    }

    internal void DisableRuntimeMonitoring()
    {
        _runtimeMonitoringEnabled = false;
        MarkRuntimeLoss(OpenXrRuntimeLossReason.ShutdownRequested);
    }

    internal void UpdateRuntimeState()
    {
        if (!_runtimeMonitoringEnabled)
            return;

        if (Window is null || Window.Renderer is null)
            return;

        if (_instance.Handle != 0 && !_sessionBegun)
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
                TearDownSessionResources(false);
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

    private void TryProbeRuntime()
    {
        if (DateTime.UtcNow < _nextProbeUtc)
            return;

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
            TearDownSessionResources(true);
        }
    }

    private void TryCreateSessionAndSwapchains(AbstractRenderer renderer)
    {
        IXrGraphicsBinding? selectedBinding = renderer switch
        {
            VulkanRenderer => new VulkanXrGraphicsBinding(),
            OpenGLRenderer => new OpenGLXrGraphicsBinding(),
            _ => null
        };

        if (selectedBinding is null)
        {
            Debug.LogWarning("OpenXR: no compatible graphics binding for the active renderer.");
            ScheduleProbeRetry();
            TearDownSessionResources(true);
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
            ScheduleProbeRetry();
            TearDownSessionResources(true);
            return;
        }

        if (renderer is OpenGLRenderer)
        {
            if (_deferredOpenGlInit is not null)
                return;

            _deferredOpenGlInit = () =>
            {
                if (Window is null || Window.Renderer is not OpenGLRenderer glRenderer)
                    return;

                if (_runtimeState != OpenXrRuntimeState.XrSystemReady)
                    return;

                Window.RenderViewportsCallback -= _deferredOpenGlInit;
                _deferredOpenGlInit = null;

                try
                {
                    _graphicsBinding.TryCreateSession(this, glRenderer);
                    CreateReferenceSpace();
                    _graphicsBinding.CreateSwapchains(this, glRenderer);
                    EnsureInputCreated();
                    SetRuntimeState(OpenXrRuntimeState.SessionCreated);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"OpenXR OpenGL session init failed: {ex.Message}");
                    ScheduleProbeRetry();
                    TearDownSessionResources(true);
                }
            };

            Window.RenderViewportsCallback += _deferredOpenGlInit;
            return;
        }

        try
        {
            _graphicsBinding.TryCreateSession(this, renderer);
            CreateReferenceSpace();
            _graphicsBinding.CreateSwapchains(this, renderer);
            EnsureInputCreated();
            SetRuntimeState(OpenXrRuntimeState.SessionCreated);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"OpenXR session init failed: {ex.Message}");
            ScheduleProbeRetry();
            TearDownSessionResources(true);
        }
    }

    private void HandleRuntimeLoss()
    {
        Debug.LogWarning($"OpenXR runtime loss detected: {_runtimeLossReason}");

        bool destroyInstance = _runtimeLossReason == OpenXrRuntimeLossReason.InstanceLostError
            || _runtimeLossReason == OpenXrRuntimeLossReason.RuntimeUnavailable;

        TearDownSessionResources(destroyInstance);
        ScheduleProbeRetry();
        SetRuntimeState(destroyInstance ? OpenXrRuntimeState.RecreatePending : OpenXrRuntimeState.DesktopOnly);
        _runtimeLossReason = OpenXrRuntimeLossReason.None;
    }

    private void ScheduleProbeRetry()
        => _nextProbeUtc = DateTime.UtcNow + _probeInterval;

    private void SetRuntimeState(OpenXrRuntimeState next)
    {
        if (_runtimeState == next)
            return;

        _runtimeState = next;
        Volatile.Write(ref _sessionRunning, next == OpenXrRuntimeState.SessionRunning ? 1 : 0);
    }

    private static bool IsSessionRunningState(SessionState state)
        => state == SessionState.Synchronized
        || state == SessionState.Visible
        || state == SessionState.Focused;

    private void MarkRuntimeLoss(OpenXrRuntimeLossReason reason)
    {
        if (reason == OpenXrRuntimeLossReason.None)
            return;

        _runtimeLossReason = reason;
        Interlocked.Exchange(ref _runtimeLossPending, 1);
    }

    private bool ConsumeRuntimeLoss(out OpenXrRuntimeLossReason reason)
    {
        if (Interlocked.Exchange(ref _runtimeLossPending, 0) == 0)
        {
            reason = OpenXrRuntimeLossReason.None;
            return false;
        }

        reason = _runtimeLossReason;
        return true;
    }

    private Result CheckResult(Result result, string operation)
    {
        if (result == Result.ErrorSessionLost)
            MarkRuntimeLoss(OpenXrRuntimeLossReason.SessionLostError);
        else if (result == Result.ErrorInstanceLost)
            MarkRuntimeLoss(OpenXrRuntimeLossReason.InstanceLostError);
        else if (result == Result.ErrorRuntimeFailure)
            MarkRuntimeLoss(OpenXrRuntimeLossReason.RuntimeUnavailable);

        return result;
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
        }
    }
}
