using Silk.NET.OpenXR;
using Silk.NET.OpenXR.Extensions.KHR;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Debug = XREngine.Debug;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    private KhrWin32ConvertPerformanceCounterTime? _win32PerformanceCounterTimeExtension;
    private int _win32PerformanceCounterTimeExtensionChecked;
    private int _win32PerformanceCounterTimeExtensionUnavailableLogged;

    private void MarkOpenXrRenderThread()
    {
        int currentId = Environment.CurrentManagedThreadId;
        int previousId = Interlocked.CompareExchange(ref _openXrRenderThreadId, currentId, 0);
        if (previousId != 0 && previousId != currentId)
            System.Diagnostics.Debug.Fail($"OpenXR render callback moved from thread {previousId} to {currentId}.");
    }

    /// <summary>
    /// Records the calling thread as the OpenXR pacing thread (set once when the dedicated pacing thread starts).
    /// </summary>
    private void MarkOpenXrPacingThread()
    {
        int currentId = Environment.CurrentManagedThreadId;
        int previousId = Interlocked.CompareExchange(ref _openXrPacingThreadId, currentId, 0);
        if (previousId != 0 && previousId != currentId)
            System.Diagnostics.Debug.Fail($"OpenXR pacing thread moved from {previousId} to {currentId}.");
    }

    /// <summary>
    /// Clears the registered OpenXR pacing thread id (call after the dedicated pacing thread exits).
    /// </summary>
    private void ClearOpenXrPacingThread()
    {
        Volatile.Write(ref _openXrPacingThreadId, 0);
    }

    [Conditional("DEBUG")]
    private void AssertOpenXrRenderThread(string operation)
    {
        int renderThreadId = Volatile.Read(ref _openXrRenderThreadId);
        int pacingThreadId = Volatile.Read(ref _openXrPacingThreadId);
        if (renderThreadId == 0 && pacingThreadId == 0)
            return;

        int currentId = Environment.CurrentManagedThreadId;
        bool ok = (renderThreadId != 0 && currentId == renderThreadId)
               || (pacingThreadId != 0 && currentId == pacingThreadId);
        System.Diagnostics.Debug.Assert(
            ok,
            $"OpenXR frame API call '{operation}' ran on thread {currentId}; expected render thread {renderThreadId} or pacing thread {pacingThreadId}.");
    }

    /// <summary>
    /// Creates an OpenXR system for the specified form factor.
    /// </summary>
    private void CreateSystem()
    {
        var systemGetInfo = new SystemGetInfo
        {
            Type = StructureType.SystemGetInfo,
            FormFactor = FormFactor.HeadMountedDisplay
        };
        var result = CheckResult(Api.GetSystem(_instance, in systemGetInfo, ref _systemId), "xrGetSystem");
        if (result != Result.Success)
        {
            throw new Exception($"Failed to get system: {result}");
        }
        RecordSmokeSystemFound();
    }

    private void CreateReferenceSpace()
    {
        var spaceCreateInfo = new ReferenceSpaceCreateInfo
        {
            Type = StructureType.ReferenceSpaceCreateInfo,
            ReferenceSpaceType = ReferenceSpaceType.Local,
            PoseInReferenceSpace = new Posef
            {
                Orientation = new Quaternionf { X = 0, Y = 0, Z = 0, W = 1 },
                Position = new Vector3f { X = 0, Y = 0, Z = 0 }
            }
        };

        Space space = default;
        if (CheckResult(Api.CreateReferenceSpace(_session, in spaceCreateInfo, ref space), "xrCreateReferenceSpace") != Result.Success)
            throw new Exception("Failed to create reference space");

        _appSpace = space;
        RecordSmokeReferenceSpaceCreated(spaceCreateInfo.ReferenceSpaceType);
    }

    /// <summary>
    /// Begins an OpenXR frame.
    /// </summary>
    /// <returns>True if the frame was successfully begun, false otherwise.</returns>
    private bool BeginFrame()
    {
        AssertOpenXrRenderThread(nameof(BeginFrame));
        var frameBeginInfo = new FrameBeginInfo { Type = StructureType.FrameBeginInfo };
        if (CheckResult(Api.BeginFrame(_session, in frameBeginInfo), "xrBeginFrame") != Result.Success)
        {
            Debug.LogWarning("Failed to begin OpenXR frame.");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Waits for the next frame timing from the OpenXR runtime.
    /// </summary>
    /// <param name="frameState">Returns the frame state information.</param>
    /// <returns>True if successfully waited for the frame, false otherwise.</returns>
    private bool WaitFrame(out FrameState frameState)
    {
        AssertOpenXrRenderThread(nameof(WaitFrame));
        var frameWaitInfo = new FrameWaitInfo { Type = StructureType.FrameWaitInfo };
        frameState = new FrameState { Type = StructureType.FrameState };
        long waitStart = Stopwatch.GetTimestamp();
        if (CheckResult(Api.WaitFrame(_session, in frameWaitInfo, ref frameState), "xrWaitFrame") != Result.Success)
        {
            Debug.LogWarning("Failed to wait for OpenXR frame.");
            return false;
        }
        long waitEnd = Stopwatch.GetTimestamp();
        long waitTicks = waitEnd - waitStart;
        RuntimeEngine.Rendering.Stats.Vr.RecordVrXrWaitFrameBlockTime(TimeSpan.FromSeconds(waitTicks / (double)Stopwatch.Frequency));

        _frameState = frameState;
        double leadMs = TryGetPredictedDisplayLeadTimeMs(frameState, waitEnd);
        RuntimeEngine.Rendering.Stats.Vr.RecordVrXrPredictedDisplayLeadTime(leadMs);

        return true;
    }

    private double TryGetPredictedDisplayLeadTimeMs(FrameState frameState, long performanceCounter)
    {
        if (!TryConvertPerformanceCounterToXrTime(performanceCounter, out long nowXrTime))
            return double.NaN;

        return (frameState.PredictedDisplayTime - nowXrTime) / 1_000_000.0;
    }

    private bool TryConvertPerformanceCounterToXrTime(long performanceCounter, out long xrTime)
    {
        xrTime = 0;
        if (!TryGetWin32PerformanceCounterTimeExtension(out var timeExtension) || timeExtension is null)
            return false;

        long counter = performanceCounter;
        Result result;
        try
        {
            result = timeExtension.ConvertWin32PerformanceCounterToTime(_instance, ref counter, ref xrTime);
        }
        catch (Exception ex)
        {
            MarkWin32PerformanceCounterTimeUnavailable($"symbol load failed: {ex.Message}");
            return false;
        }

        if (CheckResult(result, "xrConvertWin32PerformanceCounterToTimeKHR") != Result.Success)
        {
            if (result == Result.ErrorFunctionUnsupported || result == Result.ErrorValidationFailure)
                MarkWin32PerformanceCounterTimeUnavailable($"runtime returned {result}");
            return false;
        }

        return true;
    }

    private bool TryGetWin32PerformanceCounterTimeExtension(out KhrWin32ConvertPerformanceCounterTime? timeExtension)
    {
        timeExtension = _win32PerformanceCounterTimeExtension;
        if (timeExtension is not null)
            return true;

        if (Volatile.Read(ref _win32PerformanceCounterTimeExtensionChecked) != 0)
            return false;

        if (_instance.Handle == 0)
            return false;

        try
        {
            if (Api.TryGetInstanceExtension<KhrWin32ConvertPerformanceCounterTime>(string.Empty, _instance, out timeExtension))
            {
                _win32PerformanceCounterTimeExtension = timeExtension;
                return true;
            }
        }
        catch (Exception ex)
        {
            MarkWin32PerformanceCounterTimeUnavailable($"extension load failed: {ex.Message}");
            return false;
        }

        Volatile.Write(ref _win32PerformanceCounterTimeExtensionChecked, 1);
        return false;
    }

    private void MarkWin32PerformanceCounterTimeUnavailable(string reason)
    {
        _win32PerformanceCounterTimeExtension = null;
        Volatile.Write(ref _win32PerformanceCounterTimeExtensionChecked, 1);

        if (Interlocked.Exchange(ref _win32PerformanceCounterTimeExtensionUnavailableLogged, 1) == 0)
        {
            Debug.LogWarning(
                "OpenXR optional timing extension XR_KHR_win32_convert_performance_counter_time is unavailable; " +
                $"predicted display lead-time/deadline diagnostics are disabled. Reason={reason}");
        }
    }

    private void RecordDeadlineStatus(long displayTime, long submitEndCounter, uint submittedLayerCount)
    {
        if (submittedLayerCount == 0)
            return;

        if (!TryConvertPerformanceCounterToXrTime(submitEndCounter, out long submitEndXrTime))
            return;

        double safetyMarginNanoseconds = Math.Max(0.0, OpenXrDeadlineSafetyMarginMs) * 1_000_000.0;
        if (submitEndXrTime + safetyMarginNanoseconds >= displayTime)
            RuntimeEngine.Rendering.Stats.Vr.RecordVrXrMissedDeadlineFrame();
    }

    private Result EndFrameWithTiming(in FrameEndInfo frameEndInfo)
    {
        AssertOpenXrRenderThread("xrEndFrame");
        long start = Stopwatch.GetTimestamp();
        Result result;
        using (var endFrameSample = RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.XrEndFrame"))
        {
            result = CheckResult(Api.EndFrame(_session, in frameEndInfo), "xrEndFrame");
        }
        long end = Stopwatch.GetTimestamp();
        long ticks = end - start;
        RuntimeEngine.Rendering.Stats.Vr.RecordVrXrEndFrameSubmitTime(TimeSpan.FromSeconds(ticks / (double)Stopwatch.Frequency));
        RecordDeadlineStatus(frameEndInfo.DisplayTime, end, frameEndInfo.LayerCount);
        RecordSmokeEndFrame(result, frameEndInfo.LayerCount);
        return result;
    }

    private bool LocateViews()
        => LocateViews(OpenXrPoseTiming.Predicted);

    private bool LocateViews(OpenXrPoseTiming timing)
    {
        AssertOpenXrRenderThread(nameof(LocateViews));
        var displayTime = _frameState.PredictedDisplayTime;

        var viewLocateInfo = new ViewLocateInfo
        {
            Type = StructureType.ViewLocateInfo,
            DisplayTime = displayTime,
            Space = _appSpace,
            ViewConfigurationType = ViewConfigurationType.PrimaryStereo
        };

        var viewState = new ViewState { Type = StructureType.ViewState };
        uint viewCountOutput = _viewCount;
        fixed (View* viewsPtr = _views)
        {
            var viewsSpan = new Span<View>(viewsPtr, (int)_viewCount);
            if (CheckResult(Api.LocateView(_session, &viewLocateInfo, &viewState, &viewCountOutput, viewsSpan), "xrLocateViews") != Result.Success)
            {
                Debug.LogWarning("Failed to locate OpenXR views.");
                return false;
            }
        }

        if (!HandleLocatedViewState(viewState))
            return false;

        RecordSmokeLocatedViews(viewCountOutput);
        StoreViewPosesToCache(timing);
        return true;
    }

    private bool HandleLocatedViewState(ViewState viewState)
    {
        const ViewStateFlags need = ViewStateFlags.PositionValidBit | ViewStateFlags.OrientationValidBit;
        if ((viewState.ViewStateFlags & need) == need)
        {
            CacheLastValidViews();
            return true;
        }

        RuntimeEngine.Rendering.Stats.Vr.RecordVrXrTrackingLossFrame();

        // Rate-limit allocation: emit a single warning per tracking-loss streak. The flag resets when
        // CacheLastValidViews() runs again (tracking recovered). Cold-frame allocation is acceptable.
        bool firstLossInStreak = Interlocked.Exchange(ref _trackingLossStreakLogged, 1) == 0;

        switch (OpenXrTrackingLossHandling)
        {
            case OpenXrTrackingLossPolicy.FreezeLastValid:
                if (TryRestoreLastValidViews())
                    return true;
                // Freeze policy with no cached views (cold start / no successful locate yet) falls back to identity.
                // Log once per streak so users can tell why the view snapped to identity.
                if (Interlocked.Exchange(ref _freezeFallbackStreakLogged, 1) == 0)
                    Debug.LogWarning("OpenXR FreezeLastValid policy has no cached views; snapping to identity until tracking recovers.");
                ApplyIdentityViewPoses();
                return true;
            case OpenXrTrackingLossPolicy.Identity:
                ApplyIdentityViewPoses();
                return true;
            case OpenXrTrackingLossPolicy.SkipFrame:
                if (firstLossInStreak)
                    Debug.LogWarning($"OpenXR LocateViews returned invalid tracking flags: {viewState.ViewStateFlags}");
                return false;
            default:
                return false;
        }
    }

    private void CacheLastValidViews()
    {
        if (_lastValidViews is null || _lastValidViews.Length != _views.Length)
            _lastValidViews = new View[_views.Length];

        Array.Copy(_views, _lastValidViews, _views.Length);
        Volatile.Write(ref _hasLastValidViews, 1);
        // Tracking recovered — clear streak-logged flags so the next loss emits a fresh warning.
        Volatile.Write(ref _trackingLossStreakLogged, 0);
        Volatile.Write(ref _freezeFallbackStreakLogged, 0);
    }

    private bool TryRestoreLastValidViews()
    {
        View[]? lastValidViews = _lastValidViews;
        if (Volatile.Read(ref _hasLastValidViews) == 0 || lastValidViews is null || lastValidViews.Length != _views.Length)
            return false;

        Array.Copy(lastValidViews, _views, _views.Length);
        return true;
    }

    private void ApplyIdentityViewPoses()
    {
        var identity = new Posef
        {
            Orientation = new Quaternionf { W = 1.0f },
            Position = default
        };

        for (int i = 0; i < _views.Length; i++)
            _views[i].Pose = identity;
    }

    private void StoreViewPosesToCache(OpenXrPoseTiming timing)
    {
        if (_viewCount < 2)
            return;

        var l = _views[0].Pose;
        var r = _views[1].Pose;

        var lPos = new System.Numerics.Vector3(l.Position.X, l.Position.Y, l.Position.Z);
        var rPos = new System.Numerics.Vector3(r.Position.X, r.Position.Y, r.Position.Z);
        var centerPos = (lPos + rPos) * 0.5f;

        var lRot = System.Numerics.Quaternion.Normalize(new System.Numerics.Quaternion(
            l.Orientation.X,
            l.Orientation.Y,
            l.Orientation.Z,
            l.Orientation.W));

        var headLocal = System.Numerics.Matrix4x4.CreateFromQuaternion(lRot);
        headLocal.Translation = centerPos;

        var lRotM = System.Numerics.Matrix4x4.CreateFromQuaternion(lRot);
        lRotM.Translation = lPos;

        var rRot = System.Numerics.Quaternion.Normalize(new System.Numerics.Quaternion(
            r.Orientation.X,
            r.Orientation.Y,
            r.Orientation.Z,
            r.Orientation.W));
        var rRotM = System.Numerics.Matrix4x4.CreateFromQuaternion(rRot);
        rRotM.Translation = rPos;

        lock (_openXrPoseLock)
        {
            var lf = _views[0].Fov;
            var rf = _views[1].Fov;

            if (timing == OpenXrPoseTiming.Late)
            {
                _openXrLateLeftEyeLocalPose = lRotM;
                _openXrLateRightEyeLocalPose = rRotM;
                _openXrLateHeadLocalPose = headLocal;
                _openXrLateLeftEyeFov = (lf.AngleLeft, lf.AngleRight, lf.AngleUp, lf.AngleDown);
                _openXrLateRightEyeFov = (rf.AngleLeft, rf.AngleRight, rf.AngleUp, rf.AngleDown);
            }
            else
            {
                _openXrPredLeftEyeLocalPose = lRotM;
                _openXrPredRightEyeLocalPose = rRotM;
                _openXrPredHeadLocalPose = headLocal;
                _openXrPredLeftEyeFov = (lf.AngleLeft, lf.AngleRight, lf.AngleUp, lf.AngleDown);
                _openXrPredRightEyeFov = (rf.AngleLeft, rf.AngleRight, rf.AngleUp, rf.AngleDown);
            }
        }
        RecordSmokeViewPoseCache(timing);
    }

    /// <summary>
    /// Polls for OpenXR events and handles them appropriately.
    /// </summary>
    private void PollEvents()
    {
        if (_instance.Handle == 0)
            return;

        // OpenXR requires the input buffer's Type be set to EventDataBuffer.
        // The runtime then overwrites the same memory with the specific event struct.
        var eventData = new EventDataBuffer
        {
            Type = StructureType.EventDataBuffer,
            Next = null
        };

        while (true)
        {
            var result = Api.PollEvent(_instance, ref eventData);
            if (result == Result.EventUnavailable)
                break;
            if (CheckResult(result, "xrPollEvent") != Result.Success)
            {
                Debug.LogWarning($"xrPollEvent failed: {result}");
                break;
            }

            EventDataBuffer* eventDataPtr = &eventData;

            // The first field of every XrEventData* struct is StructureType.
            switch (eventData.Type)
            {
                case StructureType.EventDataSessionStateChanged:
                    {
                        var stateChanged = (EventDataSessionStateChanged*)eventDataPtr;
                        _sessionState = stateChanged->State;
                        RecordSmokeSessionState(_sessionState);
                        Debug.Out($"Session state changed to: {_sessionState}");
                        if (_sessionState == SessionState.Ready)
                        {
                            var beginInfo = new SessionBeginInfo
                            {
                                Type = StructureType.SessionBeginInfo,
                                PrimaryViewConfigurationType = ViewConfigurationType.PrimaryStereo
                            };

                            var beginResult = CheckResult(Api.BeginSession(_session, in beginInfo), "xrBeginSession");
                            Debug.Out($"xrBeginSession: {beginResult}");
                            if (beginResult == Result.Success)
                            {
                                _sessionBegun = true;
                                Debug.Out("Session began successfully");
                            }
                        }
                        else if (_sessionState == SessionState.Stopping)
                        {
                            var endResult = CheckResult(Api.EndSession(_session), "xrEndSession");
                            Debug.Out($"xrEndSession: {endResult}");
                            _sessionBegun = false;
                            StopOpenXrPacingThread();
                        }
                        else if (_sessionState == SessionState.Exiting || _sessionState == SessionState.LossPending)
                        {
                            _sessionBegun = false;
                            StopOpenXrPacingThread();
                            MarkRuntimeLoss(_sessionState == SessionState.LossPending
                                ? OpenXrRuntimeLossReason.SessionLossPending
                                : OpenXrRuntimeLossReason.SessionExiting);
                        }
                    }
                    break;
                default:
                    Debug.Out(eventData.Type.ToString());
                    break;
            }

            // Reset the buffer type for the next poll (the runtime overwrites it with the event type).
            eventData.Type = StructureType.EventDataBuffer;
            eventData.Next = null;
        }
    }

    /// <summary>
    /// Cleans up OpenXR resources.
    /// </summary>
    protected void CleanUp()
    {
        DisableRuntimeMonitoring();
        StopOpenXrPacingThread();
        StopOpenXrParallelCollectWorkers();

        if (Window is not null && _deferredOpenGlInit is not null)
            Window.RenderViewportsCallback -= _deferredOpenGlInit;
        _deferredOpenGlInit = null;

        // Break viewport/camera links.
        if (ReferenceEquals(RuntimeEngine.VRState.LeftEyeViewport, _openXrLeftViewport))
            RuntimeEngine.VRState.LeftEyeViewport = null;
        if (ReferenceEquals(RuntimeEngine.VRState.RightEyeViewport, _openXrRightViewport))
            RuntimeEngine.VRState.RightEyeViewport = null;

        _openXrLeftViewport?.Camera = null;
        _openXrRightViewport?.Camera = null;

        TearDownSessionResourcesOnOwningThread(true);

        if (_gl is not null)
        {
            if (_blitReadFbo != 0)
            {
                _gl.DeleteFramebuffer(_blitReadFbo);
                _blitReadFbo = 0;
            }
            if (_blitDrawFbo != 0)
            {
                _gl.DeleteFramebuffer(_blitDrawFbo);
                _blitDrawFbo = 0;
            }
        }

        try
        {
            _viewportMirrorFbo?.Destroy();
            _viewportMirrorFbo = null;
            _viewportMirrorDepth?.Destroy();
            _viewportMirrorDepth = null;
            _viewportMirrorColor?.Destroy();
            _viewportMirrorColor = null;
            DestroyVulkanEyeMirrorTargets();
            DestroyOpenXrPreviewTargets();
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    internal void CleanupSwapchains()
    {
        bool skippedGlFramebufferDeletes = false;
        for (int i = 0; i < _viewCount; i++)
        {
            uint[]? swapchainFramebuffers = _swapchainFramebuffers[i];
            var gl = _gl;
            if (swapchainFramebuffers is not null && gl is not null)
            {
                if (wglGetCurrentContext() != 0)
                {
                    foreach (var fbo in swapchainFramebuffers)
                    {
                        try
                        {
                            gl.DeleteFramebuffer(fbo);
                        }
                        catch (Exception ex)
                        {
                            skippedGlFramebufferDeletes = true;
                            RecordSmokeWarning($"OpenXR skipped deleting GL swapchain framebuffer {fbo}: {ex.Message}");
                            break;
                        }
                    }
                }
                else
                {
                    skippedGlFramebufferDeletes = true;
                }
            }

            if (_swapchainImagesGL[i] != null)
            {
                Marshal.FreeHGlobal((nint)_swapchainImagesGL[i]);
                _swapchainImagesGL[i] = null;
            }

            if (_swapchainImagesVK[i] != null)
            {
                Marshal.FreeHGlobal((nint)_swapchainImagesVK[i]);
                _swapchainImagesVK[i] = null;
            }

            if (_swapchainImagesDX[i] != null)
            {
                Marshal.FreeHGlobal((nint)_swapchainImagesDX[i]);
                _swapchainImagesDX[i] = null;
            }

            if (_swapchains[i].Handle != 0)
                Api.DestroySwapchain(_swapchains[i]);

            _swapchainFramebuffers[i] = null;
            _swapchainImageCounts[i] = 0;
            _swapchains[i] = default;
        }

        _viewCount = 0;

        if (skippedGlFramebufferDeletes)
            RecordSmokeWarning("OpenXR skipped one or more GL swapchain framebuffer deletes because no current OpenGL context was available during teardown.");
    }
}
