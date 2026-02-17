using Silk.NET.OpenGL;
using Silk.NET.OpenXR;
using System;
using System.Diagnostics;
using System.Threading;
using XREngine;
using XREngine.Rendering;
using XREngine.Rendering.Vulkan;
using Debug = XREngine.Debug;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    // These are invoked by Engine.VRState so OpenXR can participate in the same engine callback hooks
    // (RenderViewportsCallback, Timer.CollectVisible, Timer.SwapBuffers) as the OpenVR path.
    internal void EngineRenderTick()
        => Window_RenderViewportsCallback();

    internal void EngineCollectVisibleTick()
        => OpenXrCollectVisible();

    internal void EngineSwapBuffersTick()
        => OpenXrSwapBuffers();

    /// <summary>
    /// Renders a frame for both eyes in the XR device.
    /// This implementation supports parallel rendering of eyes when available.
    /// </summary>
    /// <param name="renderCallback">Callback function to render content to each eye's texture.</param>
    public void RenderFrame(DelRenderToFBO? renderCallback)
    {
        long submitStart = Stopwatch.GetTimestamp();
        // Render thread: only submit if the CollectVisible thread prepared a frame.
        if (!_sessionBegun)
            return;

        if (Interlocked.Exchange(ref _framePrepared, 0) == 0)
            return;

        int frameNo = Volatile.Read(ref _openXrPendingFrameNumber);
        if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
        {
            double msSinceLocate = MsSince(_openXrPrepareTimestamp);
            double msSinceCollect = MsSince(_openXrCollectTimestamp);
            double msSinceSwap = MsSince(_openXrSwapTimestamp);
            Debug.Out($"OpenXR[{frameNo}] Render: begin viewCount={_viewCount} skipRender={Volatile.Read(ref _frameSkipRender)} " +
                      $"dt(Locate={msSinceLocate:F1}ms Collect={msSinceCollect:F1}ms Swap={msSinceSwap:F1}ms)");
        }

        if (Volatile.Read(ref _frameSkipRender) != 0)
        {
            var frameEndInfoNoLayers = new FrameEndInfo
            {
                Type = StructureType.FrameEndInfo,
                DisplayTime = _frameState.PredictedDisplayTime,
                EnvironmentBlendMode = EnvironmentBlendMode.Opaque,
                LayerCount = 0,
                Layers = null
            };
            var endResult = CheckResult(Api.EndFrame(_session, in frameEndInfoNoLayers), "xrEndFrame");
            if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
                Debug.Out($"OpenXR[{frameNo}] Render: EndFrame(no layers) => {endResult}");

            Volatile.Write(ref _pendingXrFrame, 0);
            Volatile.Write(ref _pendingXrFrameCollected, 0);
            return;
        }

        var projectionViews = stackalloc CompositionLayerProjectionView[(int)_viewCount];
        for (uint i = 0; i < _viewCount; i++)
            projectionViews[i] = default;

        renderCallback ??= RenderViewportsToSwapchain;

        bool allEyesRendered = true;
        // NOTE: OpenXR swapchain acquire/wait/release is safest when done serially.
        // Parallelizing via Task.Run can break GL context ownership and runtime expectations.
        if (OpenXrDebugRenderRightThenLeft)
        {
            for (int i = (int)_viewCount - 1; i >= 0; i--)
                allEyesRendered &= RenderEye((uint)i, renderCallback, projectionViews);
        }
        else
        {
            for (uint i = 0; i < _viewCount; i++)
                allEyesRendered &= RenderEye(i, renderCallback, projectionViews);
        }

        if (_gl is not null)
            _gl.Flush();

        if (!allEyesRendered)
        {
            var frameEndInfoNoLayers = new FrameEndInfo
            {
                Type = StructureType.FrameEndInfo,
                DisplayTime = _frameState.PredictedDisplayTime,
                EnvironmentBlendMode = EnvironmentBlendMode.Opaque,
                LayerCount = 0,
                Layers = null
            };
            var endResult = CheckResult(Api.EndFrame(_session, in frameEndInfoNoLayers), "xrEndFrame");
            if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
                Debug.Out($"OpenXR[{frameNo}] Render: EndFrame(no layers; eye failure) => {endResult}");

            Volatile.Write(ref _pendingXrFrame, 0);
            Volatile.Write(ref _pendingXrFrameCollected, 0);
            return;
        }

        var layer = new CompositionLayerProjection
        {
            Type = StructureType.CompositionLayerProjection,
            Next = null,
            LayerFlags = 0,
            Space = _appSpace,
            ViewCount = _viewCount,
            Views = projectionViews
        };

        var layers = stackalloc CompositionLayerBaseHeader*[1];
        layers[0] = (CompositionLayerBaseHeader*)&layer;
        var frameEndInfo = new FrameEndInfo
        {
            Type = StructureType.FrameEndInfo,
            DisplayTime = _frameState.PredictedDisplayTime,
            EnvironmentBlendMode = EnvironmentBlendMode.Opaque,
            LayerCount = 1,
            Layers = layers
        };

        var endFrameResult = CheckResult(Api.EndFrame(_session, in frameEndInfo), "xrEndFrame");
        if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
            Debug.Out($"OpenXR[{frameNo}] Render: EndFrame(layer) => {endFrameResult}");

        long submitEnd = Stopwatch.GetTimestamp();
        double submitMs = (submitEnd - submitStart) * 1000.0 / Stopwatch.Frequency;
        Engine.Rendering.Stats.RecordVrRenderSubmitTime(TimeSpan.FromMilliseconds(submitMs));

        Volatile.Write(ref _pendingXrFrame, 0);
        Volatile.Write(ref _pendingXrFrameCollected, 0);
    }

    /// <summary>
    /// Renders a single eye (view)
    /// </summary>
    private bool RenderEye(uint viewIndex, DelRenderToFBO renderCallback, CompositionLayerProjectionView* projectionViews)
    {
        uint imageIndex = 0;
        var acquireInfo = new SwapchainImageAcquireInfo
        {
            Type = StructureType.SwapchainImageAcquireInfo
        };

        bool acquired = false;
        int frameNo = Volatile.Read(ref _openXrPendingFrameNumber);
        try
        {
            var acquireResult = CheckResult(Api.AcquireSwapchainImage(_swapchains[viewIndex], in acquireInfo, ref imageIndex), "xrAcquireSwapchainImage");
            if (acquireResult != Result.Success)
                return false;
            acquired = true;

            if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
                Debug.Out($"OpenXR[{frameNo}] Eye{viewIndex}: Acquire => {acquireResult} imageIndex={imageIndex}");

            // Wait for image ready
            var waitInfo = new SwapchainImageWaitInfo
            {
                Type = StructureType.SwapchainImageWaitInfo,
                // OpenXR timeouts are in nanoseconds. Use XR_INFINITE_DURATION (int64 max)
                // to avoid leaking an acquired image on timeout or stalling frame submission.
                Timeout = long.MaxValue
            };

            var waitResult = CheckResult(Api.WaitSwapchainImage(_swapchains[viewIndex], in waitInfo), "xrWaitSwapchainImage");
            if (waitResult != Result.Success)
                return false;

            if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
                Debug.Out($"OpenXR[{frameNo}] Eye{viewIndex}: Wait => {waitResult}");

            // Render to the texture (OpenGL path only)
            if (_gl is not null)
            {
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _swapchainFramebuffers[viewIndex][imageIndex]);
                _gl.Viewport(0, 0, _viewConfigViews[viewIndex].RecommendedImageRectWidth, _viewConfigViews[viewIndex].RecommendedImageRectHeight);

                // Guard against GL state leakage between eyes (scissor/read buffers/masks are commonly left in a bad state
                // by some passes and can make the second eye appear fully black).
                _gl.Disable(EnableCap.ScissorTest);
                _gl.ColorMask(true, true, true, true);
                _gl.DepthMask(true);

                renderCallback(_swapchainImagesGL[viewIndex][imageIndex].Image, viewIndex);

                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            }

            // Setup projection view (only if we successfully acquired+waited the swapchain image).
            projectionViews[viewIndex] = default;
            projectionViews[viewIndex].Type = StructureType.CompositionLayerProjectionView;
            projectionViews[viewIndex].Next = null;
            projectionViews[viewIndex].Fov = _views[viewIndex].Fov;
            projectionViews[viewIndex].Pose = _views[viewIndex].Pose;
            projectionViews[viewIndex].SubImage.Swapchain = _swapchains[viewIndex];
            projectionViews[viewIndex].SubImage.ImageArrayIndex = 0;
            projectionViews[viewIndex].SubImage.ImageRect = new Rect2Di
            {
                Offset = new Offset2Di { X = 0, Y = 0 },
                Extent = new Extent2Di
                {
                    Width = (int)_viewConfigViews[viewIndex].RecommendedImageRectWidth,
                    Height = (int)_viewConfigViews[viewIndex].RecommendedImageRectHeight
                }
            };

            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"OpenXR RenderEye({viewIndex}) failed: {ex.Message}");
            return false;
        }
        finally
        {
            // Always release if we acquired; otherwise the runtime can eventually stall and/or the driver can hang.
            if (acquired)
            {
                var releaseInfo = new SwapchainImageReleaseInfo { Type = StructureType.SwapchainImageReleaseInfo };
                var releaseResult = CheckResult(Api.ReleaseSwapchainImage(_swapchains[viewIndex], in releaseInfo), "xrReleaseSwapchainImage");
                if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
                    Debug.Out($"OpenXR[{frameNo}] Eye{viewIndex}: Release => {releaseResult}");
            }
        }
    }

    /// <summary>
    /// Render-thread callback that advances OpenXR state, submits the prepared frame (if any),
    /// then prepares the next frame's timing/views for the CollectVisible thread.
    /// </summary>
    private void Window_RenderViewportsCallback()
    {
        // Do NOT force a context switch here.
        // If we accidentally switch into a different (non-sharing) WGL context, engine-owned textures will become
        // invalid on this thread ("<texture> does not refer to an existing texture object"), which then cascades
        // into incomplete FBOs and black output.
        // The windowing layer should already have the correct render context current when invoking this callback.
        if (_gl is not null && OpenXrDebugGl)
        {
            nint hdcCurrent = wglGetCurrentDC();
            nint hglrcCurrent = wglGetCurrentContext();
            int dbg = Interlocked.Increment(ref _openXrDebugFrameIndex);
            if (dbg == 1 || (dbg % OpenXrDebugLogEveryNFrames) == 0)
            {
                Debug.Out(
                    $"OpenXR render thread WGL: current(HDC=0x{(nuint)hdcCurrent:X}, HGLRC=0x{(nuint)hglrcCurrent:X}) " +
                    $"session({_openXrSessionGlBindingTag}; HDC=0x{(nuint)_openXrSessionHdc:X}, HGLRC=0x{(nuint)_openXrSessionHglrc:X})");
            }
        }
        GlStateSnapshot glSnapshot = default;
        bool hasGlSnapshot = false;
        if (_gl is not null)
        {
            try
            {
                glSnapshot = GlStateSnapshot.Capture(_gl);
                hasGlSnapshot = true;
            }
            catch
            {
                // Best-effort only; fall back to sanitation on exit.
            }
        }

        try
        {
            // Keep OpenXR event/state progression on the render thread.
            PollEvents();

            // Late-update: refresh tracked poses as close to rendering as possible.
            // This updates the view poses used for projection submission and device transforms.
            if (_sessionBegun && Volatile.Read(ref _pendingXrFrame) != 0)
            {
                // Capture predicted pose before late update for debug comparison.
                System.Numerics.Matrix4x4 predHead;
                lock (_openXrPoseLock)
                    predHead = _openXrPredHeadLocalPose;

                _ = LocateViews(OpenXrPoseTiming.Late);
                UpdateActionPoseCaches(OpenXrPoseTiming.Late);

                // Debug: log pose delta between predicted and late sampling.
                int frameNo = Volatile.Read(ref _openXrPendingFrameNumber);
                if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
                {
                    System.Numerics.Matrix4x4 lateHead;
                    lock (_openXrPoseLock)
                        lateHead = _openXrLateHeadLocalPose;

                    var posDelta = lateHead.Translation - predHead.Translation;
                    float posDist = posDelta.Length() * 1000f; // mm
                    Debug.Out($"OpenXR[{frameNo}] LateUpdate: posDelta={posDist:F2}mm ({posDelta.X:F4},{posDelta.Y:F4},{posDelta.Z:F4})");
                }
            }

            // Match OpenVR timing: allow the engine to update any VR/locomotion transforms right before rendering.
            // (OpenXR runs its own render callback path, so we need to invoke the same hook here.)
            PoseTimingForRecalc = OpenXrPoseTiming.Late;
            Engine.VRState.InvokeRecalcMatrixOnDraw();

            if (!_sessionBegun)
                return;

            // Render the frame whose visibility buffers were published by the CollectVisible thread.
            RenderFrame(null);

            // After submitting the current frame (if any), prepare the next frame's timing + views.
            PrepareNextFrameOnRenderThread();
        }
        finally
        {
            // XRWindow renders desktop viewports immediately after this callback returns.
            // Restore the GL state that existed before OpenXR's work to avoid contaminating
            // the engine's normal desktop rendering. If snapshot capture failed, do best-effort sanitation.
            if (_gl is not null && hasGlSnapshot)
            {
                try
                {
                    glSnapshot.Restore(_gl);
                }
                catch
                {
                    SanitizeGlStateForEngineRendering();
                }
            }
            else
            {
                SanitizeGlStateForEngineRendering();
            }
        }
    }

    private readonly struct GlStateSnapshot
    {
        private readonly int _readFbo;
        private readonly int _drawFbo;
        private readonly int _readBuffer;
        private readonly int _currentProgram;
        private readonly int _vertexArray;
        private readonly int _arrayBuffer;
        private readonly int _activeTexture;
        private readonly bool _scissorEnabled;
        private readonly bool _blendEnabled;
        private readonly bool _depthTestEnabled;
        private readonly bool _cullFaceEnabled;

        private GlStateSnapshot(
            int readFbo,
            int drawFbo,
            int readBuffer,
            int currentProgram,
            int vertexArray,
            int arrayBuffer,
            int activeTexture,
            bool scissorEnabled,
            bool blendEnabled,
            bool depthTestEnabled,
            bool cullFaceEnabled)
        {
            _readFbo = readFbo;
            _drawFbo = drawFbo;
            _readBuffer = readBuffer;
            _currentProgram = currentProgram;
            _vertexArray = vertexArray;
            _arrayBuffer = arrayBuffer;
            _activeTexture = activeTexture;
            _scissorEnabled = scissorEnabled;
            _blendEnabled = blendEnabled;
            _depthTestEnabled = depthTestEnabled;
            _cullFaceEnabled = cullFaceEnabled;
        }

        public static GlStateSnapshot Capture(GL gl)
        {
            int readFbo = gl.GetInteger(GetPName.ReadFramebufferBinding);
            int drawFbo = gl.GetInteger(GetPName.DrawFramebufferBinding);
            int readBuffer = gl.GetInteger(GetPName.ReadBuffer);

            int currentProgram = gl.GetInteger(GetPName.CurrentProgram);
            int vertexArray = gl.GetInteger(GetPName.VertexArrayBinding);
            int arrayBuffer = gl.GetInteger(GetPName.ArrayBufferBinding);
            int activeTexture = gl.GetInteger(GetPName.ActiveTexture);

            bool scissorEnabled = gl.IsEnabled(EnableCap.ScissorTest);
            bool blendEnabled = gl.IsEnabled(EnableCap.Blend);
            bool depthTestEnabled = gl.IsEnabled(EnableCap.DepthTest);
            bool cullFaceEnabled = gl.IsEnabled(EnableCap.CullFace);

            return new GlStateSnapshot(
                readFbo,
                drawFbo,
                readBuffer,
                currentProgram,
                vertexArray,
                arrayBuffer,
                activeTexture,
                scissorEnabled,
                blendEnabled,
                depthTestEnabled,
                cullFaceEnabled);
        }

        public void Restore(GL gl)
        {
            if (_scissorEnabled)
                gl.Enable(EnableCap.ScissorTest);
            else
                gl.Disable(EnableCap.ScissorTest);

            if (_blendEnabled)
                gl.Enable(EnableCap.Blend);
            else
                gl.Disable(EnableCap.Blend);

            if (_depthTestEnabled)
                gl.Enable(EnableCap.DepthTest);
            else
                gl.Disable(EnableCap.DepthTest);

            if (_cullFaceEnabled)
                gl.Enable(EnableCap.CullFace);
            else
                gl.Disable(EnableCap.CullFace);

            gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, (uint)_readFbo);
            gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)_drawFbo);
            gl.ReadBuffer((GLEnum)_readBuffer);

            gl.UseProgram((uint)_currentProgram);
            gl.BindVertexArray((uint)_vertexArray);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, (uint)_arrayBuffer);
            gl.ActiveTexture((TextureUnit)_activeTexture);
        }
    }

    private void SanitizeGlStateForEngineRendering()
    {
        if (_gl is null)
            return;

        try
        {
            // Ensure we are back on the default framebuffer.
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

            // XRWindow already disables scissor via Renderer.SetCroppingEnabled(false),
            // but do it here as well because OpenXR renders before that point.
            _gl.Disable(EnableCap.ScissorTest);

            // Avoid leaking write masks into subsequent clears/passes.
            _gl.ColorMask(true, true, true, true);
            _gl.DepthMask(true);

            // Restore a sensible default for the default framebuffer.
            // (Some passes set ReadBuffer=None; leaving that can break later blits/copies.)
            _gl.ReadBuffer(GLEnum.Back);
            _gl.DrawBuffer(GLEnum.Back);

            // Restore viewport to the window framebuffer size so the next pass doesn't inherit swapchain sizing.
            var win = Window?.Window;
            if (win is not null)
            {
                var fb = win.FramebufferSize;
                if (fb.X > 0 && fb.Y > 0)
                    _gl.Viewport(0, 0, (uint)fb.X, (uint)fb.Y);
            }
        }
        catch
        {
            // Best-effort only; never crash the render thread for state sanitation.
        }
    }

    /// <summary>
    /// CollectVisible-thread callback.
    /// Builds per-eye visibility buffers for the OpenXR views prepared on the render thread.
    /// </summary>
    private void OpenXrCollectVisible()
    {
        // Runs on the engine's CollectVisible thread.
        // Consumes the views located on the render thread and builds per-eye visibility buffers.
        if (!_sessionBegun)
            return;

        if (Volatile.Read(ref _pendingXrFrame) == 0)
            return;

        // Avoid double-collecting if the engine calls this multiple times before SwapBuffers.
        // 0 = not started, 2 = in progress, 1 = done
        if (Interlocked.CompareExchange(ref _pendingXrFrameCollected, 2, 0) != 0)
            return;

        if (Volatile.Read(ref _frameSkipRender) != 0)
            return;

        bool success = false;
        try
        {
            int frameNo = Volatile.Read(ref _openXrPendingFrameNumber);
            if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
            {
                double msSinceLocate = MsSince(_openXrPrepareTimestamp);
                Debug.Out($"OpenXR[{frameNo}] CollectVisible: begin dt(Locate={msSinceLocate:F1}ms)");
            }

            _openXrCollectTimestamp = Stopwatch.GetTimestamp();

            var sourceViewport = TryGetSourceViewport();

            // Prefer the VRState-driven world/rig when it exists, regardless of runtime.
            // The VRState rig (HMD + per-eye cameras) should be runtime-agnostic; its transforms/params decide
            // whether to source tracking/FOV from OpenVR or OpenXR based on Engine.VRState.ActiveRuntime.
            var vrInfo = Engine.VRState.ViewInformation;
            bool hasVrRig = Engine.VRState.IsInVR &&
                            (vrInfo.World is not null || vrInfo.HMDNode is not null);
            bool canReuseVrStateEyeCameras = hasVrRig;

            // Some editor/runtime setups don't have any Window.Viewports with a World/ActiveCamera while in VR
            // (or the active viewports are UI-only). In that case, fall back to the VR rig's cameras/world.
            var baseCamera = sourceViewport?.ActiveCamera
                             ?? vrInfo.LeftEyeCamera
                             ?? vrInfo.RightEyeCamera
                             ?? _openXrFrameBaseCamera;

            var world = (hasVrRig ? vrInfo.World : null)
                        ?? sourceViewport?.World
                        ?? _openXrFrameWorld;

            if (baseCamera is null || world is null)
                return;

            _openXrFrameBaseCamera = baseCamera;
            _openXrFrameWorld = world;

            // IMPORTANT: Engine.Rendering.State.RenderingWorld (and various pipeline passes) resolve the active
            // world through RenderState.WindowViewport.World. When we pass worldOverride into CollectVisible/Render,
            // the viewport's World property still needs to return the same world or lighting can be skipped.
            _openXrLeftViewport?.WorldInstanceOverride = _openXrFrameWorld;
            _openXrRightViewport?.WorldInstanceOverride = _openXrFrameWorld;

            if (canReuseVrStateEyeCameras)
            {
                if (vrInfo.LeftEyeCamera is not null)
                    _openXrLeftEyeCamera = vrInfo.LeftEyeCamera;
                if (vrInfo.RightEyeCamera is not null)
                    _openXrRightEyeCamera = vrInfo.RightEyeCamera;
            }

            // Locomotion root maps the OpenXR tracking space into the engine world.
            // When VRState is active, use its playspace/locomotion root. Otherwise, anchor to the base camera transform
            // so the HMD view starts where the user/editor camera is.
            _openXrLocomotionRoot = (hasVrRig ? vrInfo.HMDNode?.Transform.Parent : null) ?? _openXrFrameBaseCamera.Transform;

            EnsureOpenXrEyeCameras(_openXrFrameBaseCamera);
            EnsureOpenXrViewports(
                _viewConfigViews[0].RecommendedImageRectWidth,
                _viewConfigViews[0].RecommendedImageRectHeight);

            // OpenXR must not share render pipeline *instances* with the desktop viewport.
            // But it still needs a matching pipeline type/config to avoid missing lighting/post steps.
            var sourcePipeline = sourceViewport?.RenderPipeline ?? _openXrFrameBaseCamera.RenderPipeline;
            var desiredPipeline = GetOrCreateOpenXrPipeline(sourcePipeline);

            if (!ReferenceEquals(_openXrLeftViewport!.RenderPipeline, desiredPipeline))
                _openXrLeftViewport.RenderPipeline = desiredPipeline;
            if (!ReferenceEquals(_openXrRightViewport!.RenderPipeline, desiredPipeline))
                _openXrRightViewport.RenderPipeline = desiredPipeline;

            _openXrLeftEyeCamera!.RenderPipeline = desiredPipeline;
            _openXrRightEyeCamera!.RenderPipeline = desiredPipeline;

            // Copy post-process parameters from the base camera into the per-eye cameras, but keyed by the
            // respective pipelines (desktop source pipeline -> OpenXR desired pipeline). This keeps exposure/
            // tonemapping consistent without sharing the pipeline instance.
            if (_openXrFrameBaseCamera is not null)
            {
                var postSourcePipeline = _openXrFrameBaseCamera.RenderPipeline ?? sourcePipeline;
                if (postSourcePipeline is not null)
                {
                    CopyPostProcessState(postSourcePipeline, desiredPipeline, _openXrFrameBaseCamera, _openXrLeftEyeCamera);
                    CopyPostProcessState(postSourcePipeline, desiredPipeline, _openXrFrameBaseCamera, _openXrRightEyeCamera);
                }
            }

            UpdateOpenXrEyeCameraFromView(_openXrLeftEyeCamera!, 0);
            UpdateOpenXrEyeCameraFromView(_openXrRightEyeCamera!, 1);

            // NOTE: Do not call World.Lights.UpdateCameraLightIntersections() for the OpenXR eye cameras.
            // That data is stored on the light components (not scoped per camera), so updating it here can
            // cause the desktop view's forward lighting/shadow culling to flicker while OpenXR is active.

            int leftAdded = 0;
            int rightAdded = 0;
            long leftBuildTicks = 0;
            long rightBuildTicks = 0;

            // Parallel buffer generation is only enabled on the Vulkan path.
            if (_parallelRenderingEnabled && Window?.Renderer is VulkanRenderer)
            {
                RunOpenXrParallelCollectVisible(
                    _openXrFrameWorld,
                    _openXrLeftEyeCamera,
                    _openXrRightEyeCamera,
                    out leftAdded,
                    out rightAdded,
                    out leftBuildTicks,
                    out rightBuildTicks);
            }
            else
            {
                long leftStarted = Stopwatch.GetTimestamp();
                _openXrLeftViewport!.CollectVisible(
                    collectMirrors: true,
                    worldOverride: _openXrFrameWorld,
                    cameraOverride: _openXrLeftEyeCamera,
                    allowScreenSpaceUICollectVisible: false);
                leftAdded = _openXrLeftViewport.RenderPipelineInstance.MeshRenderCommands.GetCommandsAddedCount();
                leftBuildTicks = Stopwatch.GetTimestamp() - leftStarted;

                long rightStarted = Stopwatch.GetTimestamp();
                _openXrRightViewport!.CollectVisible(
                    collectMirrors: true,
                    worldOverride: _openXrFrameWorld,
                    cameraOverride: _openXrRightEyeCamera,
                    allowScreenSpaceUICollectVisible: false);
                rightAdded = _openXrRightViewport.RenderPipelineInstance.MeshRenderCommands.GetCommandsAddedCount();
                rightBuildTicks = Stopwatch.GetTimestamp() - rightStarted;
            }

            Engine.Rendering.Stats.RecordVrPerViewVisibleCounts(
                (uint)Math.Max(0, leftAdded),
                (uint)Math.Max(0, rightAdded));
            Engine.Rendering.Stats.RecordVrPerViewDrawCounts(
                (uint)Math.Max(0, leftAdded),
                (uint)Math.Max(0, rightAdded));
            Engine.Rendering.Stats.RecordVrCommandBuildTimes(
                TimeSpan.FromSeconds(leftBuildTicks / (double)Stopwatch.Frequency),
                TimeSpan.FromSeconds(rightBuildTicks / (double)Stopwatch.Frequency));

            int dbg = Interlocked.Increment(ref _openXrDebugFrameIndex);
            if (dbg == 1 || (dbg % OpenXrDebugLogEveryNFrames) == 0)
            {
                Debug.Out($"OpenXR CollectVisible: leftAdded={leftAdded}, rightAdded={rightAdded}, CullWithFrustum(L/R)={_openXrLeftViewport!.CullWithFrustum}/{_openXrRightViewport!.CullWithFrustum}");
            }

            if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
            {
                double msSinceLocate = MsSince(_openXrPrepareTimestamp);
                double msSinceCollect = MsSince(_openXrCollectTimestamp);
                Debug.Out($"OpenXR[{frameNo}] CollectVisible: done leftAdded={leftAdded} rightAdded={rightAdded} dt(Locate={msSinceLocate:F1}ms Collect={msSinceCollect:F1}ms)");
            }

            success = true;
        }
        finally
        {
            Volatile.Write(ref _pendingXrFrameCollected, success ? 1 : 0);
        }
    }

    private void EnsureOpenXrParallelCollectWorkers()
    {
        lock (_openXrParallelCollectDispatchLock)
        {
            if (_openXrLeftCollectWorker is not null && _openXrRightCollectWorker is not null)
                return;

            _openXrParallelCollectWorkersStop = false;

            _openXrLeftCollectStart ??= new AutoResetEvent(false);
            _openXrRightCollectStart ??= new AutoResetEvent(false);
            _openXrLeftCollectDone ??= new ManualResetEventSlim(false);
            _openXrRightCollectDone ??= new ManualResetEventSlim(false);

            _openXrLeftCollectWorker = new Thread(() => OpenXrParallelCollectWorkerLoop(leftEye: true))
            {
                IsBackground = true,
                Name = "OpenXR-CollectVisible-Left"
            };
            _openXrRightCollectWorker = new Thread(() => OpenXrParallelCollectWorkerLoop(leftEye: false))
            {
                IsBackground = true,
                Name = "OpenXR-CollectVisible-Right"
            };

            _openXrLeftCollectWorker.Start();
            _openXrRightCollectWorker.Start();
        }
    }

    private void RunOpenXrParallelCollectVisible(
        XRWorldInstance world,
        XRCamera leftCamera,
        XRCamera rightCamera,
        out int leftAdded,
        out int rightAdded,
        out long leftBuildTicks,
        out long rightBuildTicks)
    {
        EnsureOpenXrParallelCollectWorkers();

        lock (_openXrParallelCollectDispatchLock)
        {
            _openXrParallelCollectWorld = world;
            _openXrParallelCollectLeftCamera = leftCamera;
            _openXrParallelCollectRightCamera = rightCamera;
            _openXrParallelCollectLeftAdded = 0;
            _openXrParallelCollectRightAdded = 0;
            _openXrParallelCollectLeftBuildTicks = 0;
            _openXrParallelCollectRightBuildTicks = 0;
            _openXrParallelCollectLeftError = null;
            _openXrParallelCollectRightError = null;

            _openXrLeftCollectDone!.Reset();
            _openXrRightCollectDone!.Reset();
        }

        _openXrLeftCollectStart!.Set();
        _openXrRightCollectStart!.Set();

        _openXrLeftCollectDone!.Wait();
        _openXrRightCollectDone!.Wait();

        lock (_openXrParallelCollectDispatchLock)
        {
            if (_openXrParallelCollectLeftError is not null || _openXrParallelCollectRightError is not null)
            {
                if (_openXrParallelCollectLeftError is not null && _openXrParallelCollectRightError is not null)
                    throw new AggregateException(_openXrParallelCollectLeftError, _openXrParallelCollectRightError);
                if (_openXrParallelCollectLeftError is not null)
                    throw _openXrParallelCollectLeftError;
                throw _openXrParallelCollectRightError!;
            }

            leftAdded = _openXrParallelCollectLeftAdded;
            rightAdded = _openXrParallelCollectRightAdded;
            leftBuildTicks = _openXrParallelCollectLeftBuildTicks;
            rightBuildTicks = _openXrParallelCollectRightBuildTicks;
        }
    }

    private void OpenXrParallelCollectWorkerLoop(bool leftEye)
    {
        AutoResetEvent? startEvent = leftEye ? _openXrLeftCollectStart : _openXrRightCollectStart;
        ManualResetEventSlim? doneEvent = leftEye ? _openXrLeftCollectDone : _openXrRightCollectDone;

        if (startEvent is null || doneEvent is null)
            return;

        while (true)
        {
            startEvent.WaitOne();

            if (_openXrParallelCollectWorkersStop)
                return;

            try
            {
                XRViewport? viewport;
                XRWorldInstance? world;
                XRCamera? camera;
                lock (_openXrParallelCollectDispatchLock)
                {
                    viewport = leftEye ? _openXrLeftViewport : _openXrRightViewport;
                    world = _openXrParallelCollectWorld;
                    camera = leftEye ? _openXrParallelCollectLeftCamera : _openXrParallelCollectRightCamera;
                }

                if (viewport is null || world is null || camera is null)
                    throw new InvalidOperationException("OpenXR parallel collect worker missing viewport/world/camera.");

                long started = Stopwatch.GetTimestamp();
                viewport.CollectVisible(
                    collectMirrors: true,
                    worldOverride: world,
                    cameraOverride: camera,
                    allowScreenSpaceUICollectVisible: false);

                int added = viewport.RenderPipelineInstance.MeshRenderCommands.GetCommandsAddedCount();
                long buildTicks = Stopwatch.GetTimestamp() - started;

                lock (_openXrParallelCollectDispatchLock)
                {
                    if (leftEye)
                    {
                        _openXrParallelCollectLeftAdded = added;
                        _openXrParallelCollectLeftBuildTicks = buildTicks;
                        _openXrParallelCollectLeftError = null;
                    }
                    else
                    {
                        _openXrParallelCollectRightAdded = added;
                        _openXrParallelCollectRightBuildTicks = buildTicks;
                        _openXrParallelCollectRightError = null;
                    }
                }
            }
            catch (Exception ex)
            {
                lock (_openXrParallelCollectDispatchLock)
                {
                    if (leftEye)
                        _openXrParallelCollectLeftError = ex;
                    else
                        _openXrParallelCollectRightError = ex;
                }
            }
            finally
            {
                doneEvent.Set();
            }
        }
    }

    private void StopOpenXrParallelCollectWorkers()
    {
        Thread? leftWorker;
        Thread? rightWorker;
        AutoResetEvent? leftStart;
        AutoResetEvent? rightStart;
        ManualResetEventSlim? leftDone;
        ManualResetEventSlim? rightDone;

        lock (_openXrParallelCollectDispatchLock)
        {
            _openXrParallelCollectWorkersStop = true;

            leftWorker = _openXrLeftCollectWorker;
            rightWorker = _openXrRightCollectWorker;
            leftStart = _openXrLeftCollectStart;
            rightStart = _openXrRightCollectStart;
            leftDone = _openXrLeftCollectDone;
            rightDone = _openXrRightCollectDone;

            _openXrLeftCollectWorker = null;
            _openXrRightCollectWorker = null;
            _openXrLeftCollectStart = null;
            _openXrRightCollectStart = null;
            _openXrLeftCollectDone = null;
            _openXrRightCollectDone = null;
        }

        leftStart?.Set();
        rightStart?.Set();

        leftWorker?.Join(250);
        rightWorker?.Join(250);

        leftDone?.Dispose();
        rightDone?.Dispose();
        leftStart?.Dispose();
        rightStart?.Dispose();
    }

    /// <summary>
    /// CollectVisible-thread callback.
    /// Publishes the per-eye buffers to the render thread (sync point between CollectVisible and rendering).
    /// </summary>
    private void OpenXrSwapBuffers()
    {
        // Runs on the engine's CollectVisible thread, after the previous render completes.
        // Acts as the sync point between CollectVisible (buffer generation) and the render thread.
        if (!_sessionBegun)
            return;

        if (Volatile.Read(ref _pendingXrFrame) == 0)
            return;

        // If the runtime says "do not render", just let the render thread EndFrame with no layers.
        if (Volatile.Read(ref _frameSkipRender) != 0)
        {
            Interlocked.Exchange(ref _framePrepared, 1);
            return;
        }

        // If we didn't successfully collect this frame, don't try to render stale buffers.
        if (Volatile.Read(ref _pendingXrFrameCollected) != 1)
            return;

        int frameNo = Volatile.Read(ref _openXrPendingFrameNumber);
        if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
        {
            double msSinceLocate = MsSince(_openXrPrepareTimestamp);
            double msSinceCollect = MsSince(_openXrCollectTimestamp);
            Debug.Out($"OpenXR[{frameNo}] SwapBuffers: publishing eye buffers dt(Locate={msSinceLocate:F1}ms Collect={msSinceCollect:F1}ms)");
        }

        _openXrSwapTimestamp = Stopwatch.GetTimestamp();

        _openXrLeftViewport?.SwapBuffers(allowScreenSpaceUISwap: false);
        _openXrRightViewport?.SwapBuffers(allowScreenSpaceUISwap: false);

        Interlocked.Exchange(ref _framePrepared, 1);

        if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
            Debug.Out($"OpenXR[{frameNo}] SwapBuffers: framePrepared=1");
    }

    /// <summary>
    /// Render-thread helper.
    /// Waits/begins an OpenXR frame and locates views, making that frame available for CollectVisible.
    /// </summary>
    private void PrepareNextFrameOnRenderThread()
    {
        // Called on the render thread. Prepares the next OpenXR frame (WaitFrame/BeginFrame/LocateViews)
        // so the CollectVisible thread can build buffers for it.
        if (!_sessionBegun)
            return;

        // Only one OpenXR frame can be "in flight" between BeginFrame and EndFrame.
        if (Volatile.Read(ref _pendingXrFrame) != 0)
            return;

        // Clear any stale publish flags.
        Volatile.Write(ref _framePrepared, 0);
        Volatile.Write(ref _pendingXrFrameCollected, 0);

        if (!WaitFrame(out _frameState))
            return;

        if (!BeginFrame())
            return;

        int frameNo = Interlocked.Increment(ref _openXrLifecycleFrameIndex);
        Volatile.Write(ref _openXrPendingFrameNumber, frameNo);

        if (OpenXrDebugLifecycle && ShouldLogLifecycle(frameNo))
        {
            Debug.Out($"OpenXR[{frameNo}] Prepare: Wait+Begin ok predictedDisplayTime={_frameState.PredictedDisplayTime} shouldRender={_frameState.ShouldRender}");
        }

        if (_frameState.ShouldRender == 0)
        {
            Volatile.Write(ref _frameSkipRender, 1);
            Volatile.Write(ref _pendingXrFrame, 1);

            if (OpenXrDebugLifecycle && ShouldLogLifecycle(frameNo))
                Debug.Out($"OpenXR[{frameNo}] Prepare: ShouldRender=0 (will EndFrame with no layers)");
            return;
        }

        Volatile.Write(ref _frameSkipRender, 0);

        // Predicted views for the upcoming frame (used by CollectVisible + update-thread consumers).
        if (!LocateViews(OpenXrPoseTiming.Predicted))
        {
            EndBegunFrameWithoutLayers(frameNo, "LocateViews failed");
            return;
        }

        try
        {
            // Predicted controller/tracker poses for the upcoming frame.
            UpdateActionPoseCaches(OpenXrPoseTiming.Predicted);

            // The CollectVisible thread will consume this frame's predicted views.
            // Update the VR rig immediately after LocateViews so any VRState-provided cameras/transforms
            // reflect the same predicted pose when building visibility buffers.
            PoseTimingForRecalc = OpenXrPoseTiming.Predicted;
            Engine.VRState.InvokeRecalcMatrixOnDraw();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"OpenXR[{frameNo}] Prepare: predicted pose update failed: {ex.Message}");
            EndBegunFrameWithoutLayers(frameNo, "Predicted pose update failed");
            return;
        }

        _openXrPrepareTimestamp = Stopwatch.GetTimestamp();

        if (OpenXrDebugLifecycle && ShouldLogLifecycle(frameNo) && _views.Length >= 2)
        {
            var l = _views[0];
            var r = _views[1];
            Debug.Out(
                $"OpenXR[{frameNo}] Prepare: LocateViews ok " +
                $"L(pos={l.Pose.Position.X:F3},{l.Pose.Position.Y:F3},{l.Pose.Position.Z:F3}) " +
                $"R(pos={r.Pose.Position.X:F3},{r.Pose.Position.Y:F3},{r.Pose.Position.Z:F3})");
        }

        Volatile.Write(ref _pendingXrFrame, 1);
    }

    private void EndBegunFrameWithoutLayers(int frameNo, string reason)
    {
        try
        {
            var frameEndInfoNoLayers = new FrameEndInfo
            {
                Type = StructureType.FrameEndInfo,
                DisplayTime = _frameState.PredictedDisplayTime,
                EnvironmentBlendMode = EnvironmentBlendMode.Opaque,
                LayerCount = 0,
                Layers = null
            };

            var endResult = CheckResult(Api.EndFrame(_session, in frameEndInfoNoLayers), "xrEndFrame");
            if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
                Debug.Out($"OpenXR[{frameNo}] Prepare: aborted frame ({reason}), EndFrame(no layers) => {endResult}");
        }
        finally
        {
            Volatile.Write(ref _framePrepared, 0);
            Volatile.Write(ref _frameSkipRender, 0);
            Volatile.Write(ref _pendingXrFrame, 0);
            Volatile.Write(ref _pendingXrFrameCollected, 0);
        }
    }
}
