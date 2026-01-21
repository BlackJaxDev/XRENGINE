using Silk.NET.OpenXR;
using System;
using System.Runtime.InteropServices;
using Debug = XREngine.Debug;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
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
    }

    /// <summary>
    /// Begins an OpenXR frame.
    /// </summary>
    /// <returns>True if the frame was successfully begun, false otherwise.</returns>
    private bool BeginFrame()
    {
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
        var frameWaitInfo = new FrameWaitInfo { Type = StructureType.FrameWaitInfo };
        frameState = new FrameState { Type = StructureType.FrameState };
        if (CheckResult(Api.WaitFrame(_session, in frameWaitInfo, ref frameState), "xrWaitFrame") != Result.Success)
        {
            Debug.LogWarning("Failed to wait for OpenXR frame.");
            return false;
        }
        _frameState = frameState;
        return true;
    }

    private bool LocateViews()
        => LocateViews(OpenXrPoseTiming.Predicted);

    private bool LocateViews(OpenXrPoseTiming timing)
    {
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

        StoreViewPosesToCache(timing);
        return true;
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
                        }
                        else if (_sessionState == SessionState.Exiting || _sessionState == SessionState.LossPending)
                        {
                            _sessionBegun = false;
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

        if (Window is not null && _deferredOpenGlInit is not null)
            Window.RenderViewportsCallback -= _deferredOpenGlInit;
        _deferredOpenGlInit = null;

        // Break viewport/camera links.
        _openXrLeftViewport?.Camera = null;
        _openXrRightViewport?.Camera = null;

        TearDownSessionResources(true);

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
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    internal void CleanupSwapchains()
    {
        for (int i = 0; i < _viewCount; i++)
        {
            if (_swapchainFramebuffers[i] is not null && _gl is not null)
                foreach (var fbo in _swapchainFramebuffers[i])
                    _gl.DeleteFramebuffer(fbo);

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
    }
}
