using Silk.NET.OpenGL;
using Silk.NET.OpenXR;
using System;
using System.Diagnostics;
using System.Threading;
using XREngine;
using XREngine.Data.Geometry;
using XREngine.Input;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Vulkan;
using Debug = XREngine.Debug;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    // These are invoked by RuntimeEngine.VRState so OpenXR can participate in the same engine callback hooks
    // (RenderViewportsCallback, Timer.CollectVisible, Timer.SwapBuffers) as the OpenVR path.
    internal void EngineRenderTick()
        => Window_RenderViewportsCallback();

    internal void EnginePostRenderTick()
        => Window_PostRenderViewportsCallback();

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
        AssertOpenXrRenderThread(nameof(RenderFrame));
        long submitStart = Stopwatch.GetTimestamp();
        // Render thread: only submit if the CollectVisible thread prepared a frame.
        if (!_sessionBegun)
            return;

        if (Interlocked.Exchange(ref _framePrepared, 0) == 0)
        {
            // In DedicatedThread mode the pacing thread is responsible for publishing the next frame.
            // If we got here without one, the render thread is effectively starved by the pacing thread.
            if (OpenXrRenderPacingHandling == OpenXrRenderPacingMode.DedicatedThread && _sessionBegun)
                RuntimeEngine.Rendering.Stats.Vr.RecordVrXrPacingHandoffStall();
            return;
        }

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
            var endResult = EndFrameWithTiming(in frameEndInfoNoLayers);
            if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
                Debug.Out($"OpenXR[{frameNo}] Render: EndFrame(no layers) => {endResult}");

            Volatile.Write(ref _pendingXrFrame, 0);
            Volatile.Write(ref _pendingXrFrameCollected, 0);
            SignalPacingThreadFrameSubmitted();
            return;
        }

        if (!TryResolveOpenXrViewRenderModeForCurrentBackend(out _))
        {
            var frameEndInfoNoLayers = new FrameEndInfo
            {
                Type = StructureType.FrameEndInfo,
                DisplayTime = _frameState.PredictedDisplayTime,
                EnvironmentBlendMode = EnvironmentBlendMode.Opaque,
                LayerCount = 0,
                Layers = null
            };
            var endResult = EndFrameWithTiming(in frameEndInfoNoLayers);
            if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
                Debug.Out($"OpenXR[{frameNo}] Render: EndFrame(no layers; unsupported view render mode) => {endResult}");

            Volatile.Write(ref _pendingXrFrame, 0);
            Volatile.Write(ref _pendingXrFrameCollected, 0);
            SignalPacingThreadFrameSubmitted();
            return;
        }

        var projectionViews = stackalloc CompositionLayerProjectionView[(int)_viewCount];
        for (uint i = 0; i < _viewCount; i++)
            projectionViews[i] = default;
        ResetOpenXrRvcFrameProfile();

        renderCallback ??= RenderViewportsToSwapchain;

        bool allEyesRendered;
        bool vulkanBatchHandled;
        using (var batchSample = RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.RenderFrame.TryRenderVulkanEyesBatch"))
        {
            allEyesRendered = TryRenderVulkanEyesBatch(projectionViews, out vulkanBatchHandled);
        }

        if (!allEyesRendered && !vulkanBatchHandled && OpenXrDebugRenderRightThenLeft)
        {
            allEyesRendered = true;
            using (var sequentialSample = RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.RenderFrame.RenderEyesSequential"))
            {
                for (int i = (int)_viewCount - 1; i >= 0; i--)
                    allEyesRendered &= RenderEye((uint)i, renderCallback, projectionViews);
            }
        }
        else if (!allEyesRendered && !vulkanBatchHandled)
        {
            allEyesRendered = true;
            using (var sequentialSample = RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.RenderFrame.RenderEyesSequential"))
            {
                for (uint i = 0; i < _viewCount; i++)
                    allEyesRendered &= RenderEye(i, renderCallback, projectionViews);
            }
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
            var endResult = EndFrameWithTiming(in frameEndInfoNoLayers);
            if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
                Debug.Out($"OpenXR[{frameNo}] Render: EndFrame(no layers; eye failure) => {endResult}");

            Volatile.Write(ref _pendingXrFrame, 0);
            Volatile.Write(ref _pendingXrFrameCollected, 0);
            SignalPacingThreadFrameSubmitted();
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

        var endFrameResult = EndFrameWithTiming(in frameEndInfo);
        if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
            Debug.Out($"OpenXR[{frameNo}] Render: EndFrame(layer) => {endFrameResult}");
        PublishOpenXrRvcFrameProfile(frameNo, endFrameResult);

        long submitEnd = Stopwatch.GetTimestamp();
        double submitMs = (submitEnd - submitStart) * 1000.0 / Stopwatch.Frequency;
        RuntimeEngine.Rendering.Stats.Vr.RecordVrRenderSubmitTime(TimeSpan.FromMilliseconds(submitMs));

        Volatile.Write(ref _pendingXrFrame, 0);
        Volatile.Write(ref _pendingXrFrameCollected, 0);
        SignalPacingThreadFrameSubmitted();
    }

    /// <summary>
    /// Renders a single eye (view)
    /// </summary>
    private bool RenderEye(uint viewIndex, DelRenderToFBO renderCallback, CompositionLayerProjectionView* projectionViews)
    {
        AssertOpenXrRenderThread(nameof(RenderEye));
        uint imageIndex = 0;
        var acquireInfo = new SwapchainImageAcquireInfo
        {
            Type = StructureType.SwapchainImageAcquireInfo
        };

        bool acquired = false;
        int frameNo = Volatile.Read(ref _openXrPendingFrameNumber);
        try
        {
            if (Window?.Renderer is VulkanRenderer && ShouldPrewarmVulkanEyeResources(viewIndex))
                PrewarmVulkanEyeResources(viewIndex);

            var acquireResult = CheckResult(Api.AcquireSwapchainImage(_swapchains[viewIndex], in acquireInfo, ref imageIndex), "xrAcquireSwapchainImage");
            if (acquireResult != Result.Success)
                return false;
            acquired = true;
            RecordSmokeEyeAcquire(viewIndex);

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
            RecordSmokeEyeWait(viewIndex);

            if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
                Debug.Out($"OpenXR[{frameNo}] Eye{viewIndex}: Wait => {waitResult}");

            // Render to the acquired swapchain image.
            GL? gl = _gl;
            uint[]? swapchainFramebuffers = _swapchainFramebuffers[viewIndex];
            SwapchainImageOpenGLKHR* swapchainImages = _swapchainImagesGL[viewIndex];
            if (gl is not null && swapchainFramebuffers is not null && swapchainImages != null)
            {
                if (imageIndex >= swapchainFramebuffers.Length)
                    throw new InvalidOperationException($"OpenXR acquired swapchain image index {imageIndex}, but view {viewIndex} only has {swapchainFramebuffers.Length} OpenGL framebuffers.");

                _openXrCurrentSwapchainFramebuffer = swapchainFramebuffers[imageIndex];
                try
                {
                    gl.BindFramebuffer(FramebufferTarget.Framebuffer, _openXrCurrentSwapchainFramebuffer);
                    gl.Viewport(0, 0, GetOpenXrSwapchainWidth(viewIndex), GetOpenXrSwapchainHeight(viewIndex));

                    // Guard against GL state leakage between eyes (scissor/read buffers/masks are commonly left in a bad state
                    // by some passes and can make the second eye appear fully black).
                    gl.Disable(EnableCap.ScissorTest);
                    gl.ColorMask(true, true, true, true);
                    gl.DepthMask(true);

                    renderCallback(swapchainImages[imageIndex].Image, viewIndex);
                }
                finally
                {
                    _openXrCurrentSwapchainFramebuffer = 0;
                    gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                }
            }
            else if (Window?.Renderer is VulkanRenderer)
            {
                if (!TryRenderVulkanEye(viewIndex, imageIndex))
                    return false;
            }
            else
            {
                Debug.LogWarning($"OpenXR RenderEye({viewIndex}) has no compatible graphics swapchain renderer.");
                return false;
            }

            // Setup projection view (only if we successfully acquired+waited the swapchain image).
            FillProjectionView(viewIndex, projectionViews);

            return true;
        }
        catch (InvalidOperationException)
        {
            throw;
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
                if (releaseResult == Result.Success)
                    RecordSmokeEyeRelease(viewIndex);
                if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
                    Debug.Out($"OpenXR[{frameNo}] Eye{viewIndex}: Release => {releaseResult}");
            }
        }
    }

    private void FillProjectionView(uint viewIndex, CompositionLayerProjectionView* projectionViews)
    {
        uint expectedWidth = GetOpenXrSwapchainWidth(viewIndex);
        uint expectedHeight = GetOpenXrSwapchainHeight(viewIndex);
        View view = TryGetOpenXrProjectionLayerView(viewIndex, out View cachedView)
            ? cachedView
            : _views[viewIndex];

        projectionViews[viewIndex] = default;
        projectionViews[viewIndex].Type = StructureType.CompositionLayerProjectionView;
        projectionViews[viewIndex].Next = null;
        projectionViews[viewIndex].Fov = view.Fov;
        projectionViews[viewIndex].Pose = view.Pose;
        projectionViews[viewIndex].SubImage.Swapchain = _swapchains[viewIndex];
        projectionViews[viewIndex].SubImage.ImageArrayIndex = 0;
        projectionViews[viewIndex].SubImage.ImageRect = new Rect2Di
        {
            Offset = new Offset2Di { X = 0, Y = 0 },
            Extent = new Extent2Di
            {
                Width = checked((int)expectedWidth),
                Height = checked((int)expectedHeight)
            }
        };

        ValidateProjectionViewSubImage(viewIndex, in projectionViews[viewIndex], expectedWidth, expectedHeight);
        TraceProjectionViewSubImage(viewIndex, in projectionViews[viewIndex], expectedWidth, expectedHeight);
        RecordOpenXrRvcFrameViewDiagnostics(viewIndex, expectedWidth, expectedHeight);
    }

    private void ResetOpenXrRvcFrameProfile()
    {
        _openXrRvcProfiledViewMask = 0u;
        int count = (int)Math.Min(_viewCount, (uint)_openXrRvcFrameViewDiagnostics.Length);
        for (int i = 0; i < count; i++)
        {
            _openXrRvcFrameViewDiagnostics[i] = default;
            _openXrRvcFrameViewProjectionDiagnostics[i] = RvcFrameViewProjectionDiagnostics.Empty;
        }
    }

    private void RecordOpenXrRvcFrameViewDiagnostics(uint viewIndex, uint width, uint height)
    {
        if (viewIndex >= _openXrRvcFrameViewDiagnostics.Length || viewIndex >= _views.Length)
            return;

        View view = TryGetOpenXrProjectionLayerView(viewIndex, out View cachedView)
            ? cachedView
            : _views[viewIndex];
        const float radiansToDegrees = 180.0f / MathF.PI;
        float horizontalFovDegrees = MathF.Abs(view.Fov.AngleRight - view.Fov.AngleLeft) * radiansToDegrees;
        float verticalFovDegrees = MathF.Abs(view.Fov.AngleUp - view.Fov.AngleDown) * radiansToDegrees;
        IRuntimeRenderingHostServices host = RuntimeRenderingHostServices.Current;
        double gpuMilliseconds = host.ResolveRvcViewGpuMilliseconds(
            checked((int)viewIndex),
            checked((int)Math.Min(_viewCount, (uint)_openXrRvcFrameViewDiagnostics.Length)));

        _openXrRvcFrameViewDiagnostics[viewIndex] = new(
            viewIndex,
            ResolveOpenXrRvcViewKind(viewIndex),
            width,
            height,
            horizontalFovDegrees,
            verticalFovDegrees,
            _swapchains[viewIndex].Handle,
            (ulong)width * height,
            gpuMilliseconds,
            host.VrViewRenderMode,
            host.VrFoveationMode,
            _activeViewConfigurationFallbackReason);
        RecordOpenXrRvcFrameViewProjectionDiagnostics(viewIndex, width, height);
        _openXrRvcProfiledViewMask |= 1u << (int)viewIndex;
    }

    private void RecordOpenXrRvcFrameViewProjectionDiagnostics(uint viewIndex, uint width, uint height)
    {
        if (viewIndex >= _openXrRvcFrameViewProjectionDiagnostics.Length)
            return;

        XRCamera? camera = GetOpenXrEyeCamera(viewIndex);
        Matrix4x4 viewMatrix = camera?.Transform.InverseRenderMatrix ?? Matrix4x4.Identity;
        Matrix4x4 projectionMatrix = camera?.ProjectionMatrix ?? Matrix4x4.Identity;
        Matrix4x4 viewProjectionMatrix = camera?.ViewProjectionMatrix ?? viewMatrix * projectionMatrix;
        Matrix4x4 previousViewProjectionMatrix = _openXrRvcPreviousViewProjectionMatrices[viewIndex];
        if (previousViewProjectionMatrix == default)
            previousViewProjectionMatrix = Matrix4x4.Identity;

        _openXrRvcFrameViewProjectionDiagnostics[viewIndex] = new(
            viewIndex,
            RuntimeViewIndex: checked((int)viewIndex),
            ViewportX: 0,
            ViewportY: 0,
            ViewportWidth: checked((int)width),
            ViewportHeight: checked((int)height),
            viewMatrix,
            projectionMatrix,
            viewProjectionMatrix,
            previousViewProjectionMatrix);
        _openXrRvcPreviousViewProjectionMatrices[viewIndex] = viewProjectionMatrix;
    }

    private void PublishOpenXrRvcFrameProfile(int frameNo, Result endFrameResult)
    {
        uint count = Math.Min(_viewCount, (uint)_openXrRvcFrameViewDiagnostics.Length);
        if (count == 0 || _openXrRvcProfiledViewMask == 0u)
            return;

        ERvcFallbackReason fallbackReason = _activeViewConfigurationFallbackReason;
        string diagnostic = _activeViewConfigurationDiagnostic;
        if (endFrameResult != Result.Success)
        {
            fallbackReason = ERvcFallbackReason.HardwareValidationUnavailable;
            diagnostic = $"OpenXR xrEndFrame returned {endFrameResult} for frame {frameNo}.";
        }

        RvcFrameProfileSnapshot profile = RvcFrameProfileSnapshot.Create(
            RuntimeRenderingHostServices.Current.CurrentRenderFrameId,
            _frameState.PredictedDisplayTime,
            _openXrRvcFrameViewDiagnostics.AsSpan(0, (int)count),
            _openXrRvcFrameViewProjectionDiagnostics.AsSpan(0, (int)count),
            fallbackReason,
            diagnostic);
        RuntimeRenderingHostServices.Current.RecordRenderRvcFrameProfile(profile);
    }

    private void ValidateProjectionViewSubImage(
        uint viewIndex,
        in CompositionLayerProjectionView projectionView,
        uint expectedWidth,
        uint expectedHeight)
    {
        if (viewIndex >= _viewCount)
            throw new InvalidOperationException($"OpenXR projection view index {viewIndex} exceeds active view count {_viewCount}.");

        if (projectionView.SubImage.Swapchain.Handle == 0)
            throw new InvalidOperationException($"OpenXR projection view {viewIndex} has no swapchain bound.");

        if (projectionView.SubImage.Swapchain.Handle != _swapchains[viewIndex].Handle)
        {
            throw new InvalidOperationException(
                $"OpenXR projection view {viewIndex} is bound to the wrong swapchain. " +
                $"Expected=0x{_swapchains[viewIndex].Handle:X}; Actual=0x{projectionView.SubImage.Swapchain.Handle:X}.");
        }

        Rect2Di rect = projectionView.SubImage.ImageRect;
        if (rect.Offset.X != 0 || rect.Offset.Y != 0 ||
            rect.Extent.Width != checked((int)expectedWidth) ||
            rect.Extent.Height != checked((int)expectedHeight))
        {
            throw new InvalidOperationException(
                $"OpenXR projection view {viewIndex} sub-image does not cover the full eye swapchain. " +
                $"Expected=(0,0,{expectedWidth}x{expectedHeight}); " +
                $"Actual=({rect.Offset.X},{rect.Offset.Y},{rect.Extent.Width}x{rect.Extent.Height}).");
        }
    }

    private void TraceProjectionViewSubImage(
        uint viewIndex,
        in CompositionLayerProjectionView projectionView,
        uint expectedWidth,
        uint expectedHeight)
    {
        if (!VulkanCaptureEyeOutputs && !OpenXrDebugLifecycle)
            return;

        int frameNo = Volatile.Read(ref _openXrPendingFrameNumber);
        if (frameNo != 0 && !ShouldLogLifecycle(frameNo))
            return;

        Rect2Di rect = projectionView.SubImage.ImageRect;
        Debug.Out(
            $"OpenXR[{frameNo}] ProjectionView: eye={viewIndex} swapchain=0x{projectionView.SubImage.Swapchain.Handle:X} " +
            $"rect=({rect.Offset.X},{rect.Offset.Y},{rect.Extent.Width}x{rect.Extent.Height}) " +
            $"expected={expectedWidth}x{expectedHeight} arrayIndex={projectionView.SubImage.ImageArrayIndex}");
    }

    /// <summary>
    /// Render-thread callback that advances OpenXR state, submits the prepared frame (if any),
    /// then prepares the next frame's timing/views for the CollectVisible thread.
    /// </summary>
    private void Window_RenderViewportsCallback()
    {
        MarkOpenXrRenderThread();
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
            using (var pollEventsSample = RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.RenderCallback.PollEvents"))
            {
                PollEvents();
            }

            // Late-update: refresh tracked poses as close to rendering as possible.
            // This updates the view poses used for projection submission and device transforms.
            if (_sessionBegun && Volatile.Read(ref _pendingXrFrame) != 0)
            {
                using var latePoseSample = RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.RenderCallback.LatePose");
                // Capture predicted pose before late update for debug comparison.
                System.Numerics.Matrix4x4 predHead;
                lock (_openXrPoseLock)
                    predHead = _openXrPredHeadLocalPose;

                _ = LocateViews(OpenXrPoseTiming.Late);
                UpdateActionPoseCaches(OpenXrPoseTiming.Late);

                System.Numerics.Matrix4x4 lateHead;
                lock (_openXrPoseLock)
                    lateHead = _openXrLateHeadLocalPose;

                var (posDist, rotDeg) = ComputePoseDelta(predHead, lateHead);
                RuntimeEngine.Rendering.Stats.Vr.RecordVrXrPredictedToLatePoseDelta(posDist, rotDeg);

                // Debug: log pose delta between predicted and late sampling.
                int frameNo = Volatile.Read(ref _openXrPendingFrameNumber);
                if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
                {
                    var posDelta = lateHead.Translation - predHead.Translation;
                    Debug.Out($"OpenXR[{frameNo}] LateUpdate: posDelta={posDist:F2}mm rotDelta={rotDeg:F2}deg ({posDelta.X:F4},{posDelta.Y:F4},{posDelta.Z:F4})");
                }
            }

            // Do not publish late OpenXR poses through the scene-wide render-matrix hook here.
            // XRWindow renders the independent desktop viewport immediately after this callback,
            // while the collect thread may already be building the next desktop visible set.
            // Mutating shared HMD/controller render matrices at this point can make desktop
            // collect/render observe different camera state. Eye rendering consumes the late
            // pose directly in ApplyOpenXrEyePoseForRenderThread instead.

            if (!_sessionBegun)
                return;

            // Lazily start the dedicated pacing thread when the session is ready and the mode is enabled.
            // The render thread then only signals it after xrEndFrame instead of running prep inline.
            using (var pacingStartSample = RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.RenderCallback.EnsurePacingThread"))
            {
                EnsureOpenXrPacingThreadStarted();
            }

            // Render the frame whose visibility buffers were published by the CollectVisible thread.
            using (var renderFrameSample = RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.RenderCallback.RenderFrame"))
            {
                RenderFrame(null);
            }

            // Inline prep (pre-desktop-render) is the legacy InRenderCallback path.
            if (OpenXrRenderPacingHandling == OpenXrRenderPacingMode.InRenderCallback)
            {
                using var prepSample = RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.RenderCallback.PrepareNextFrame");
                PrepareNextFrameForPacingOwner();
            }
        }
        finally
        {
            using var restoreStateSample = RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.RenderCallback.RestoreRenderState");
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

    private void Window_PostRenderViewportsCallback()
    {
        MarkOpenXrRenderThread();

        // PostRenderCallback (default) runs prep inline here; InRenderCallback already ran it pre-desktop;
        // DedicatedThread mode leaves prep to the pacing thread (signaled after xrEndFrame in RenderFrame).
        if (OpenXrRenderPacingHandling == OpenXrRenderPacingMode.PostRenderCallback
            && OpenXrPrepareFrameAfterDesktopRender)
        {
            using var prepSample = RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.PostRender.PrepareNextFrame");
            PrepareNextFrameForPacingOwner();
        }
    }

    private static (double Millimeters, double Degrees) ComputePoseDelta(System.Numerics.Matrix4x4 predicted, System.Numerics.Matrix4x4 late)
    {
        var posDelta = late.Translation - predicted.Translation;
        double millimeters = posDelta.Length() * 1000.0;

        System.Numerics.Quaternion predRot = System.Numerics.Quaternion.Normalize(System.Numerics.Quaternion.CreateFromRotationMatrix(predicted));
        System.Numerics.Quaternion lateRot = System.Numerics.Quaternion.Normalize(System.Numerics.Quaternion.CreateFromRotationMatrix(late));
        System.Numerics.Quaternion delta = System.Numerics.Quaternion.Normalize(System.Numerics.Quaternion.Inverse(predRot) * lateRot);
        double w = Math.Clamp(Math.Abs(delta.W), 0.0, 1.0);
        double degrees = 2.0 * Math.Acos(w) * (180.0 / Math.PI);
        return (millimeters, degrees);
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
    /// Builds per-eye visibility buffers for the prepared OpenXR views.
    /// </summary>
    private void OpenXrCollectVisible()
    {
        // Runs on the engine's CollectVisible thread.
        // Consumes the located views and builds per-eye visibility buffers.
        if (!_sessionBegun)
            return;

        if (OpenXrRenderPacingHandling == OpenXrRenderPacingMode.CollectVisibleThread)
        {
            if (TryBeginOpenXrCollectVisiblePrepThread())
            {
                try
                {
                    using var prepSample = RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.CollectVisible.PrepareNextFrame");
                    PrepareNextFrameForPacingOwner();
                }
                finally
                {
                    ClearOpenXrCollectVisiblePrepThread();
                }
            }
        }

        if (Volatile.Read(ref _pendingXrFrame) == 0)
            return;

        // Avoid double-collecting if the engine calls this multiple times before SwapBuffers.
        // 0 = not started, 2 = in progress, 1 = done
        if (Interlocked.CompareExchange(ref _pendingXrFrameCollected, 2, 0) != 0)
            return;

        Volatile.Write(ref _pendingXrFrameUsesTrueSinglePassStereo, 0);

        if (Volatile.Read(ref _frameSkipRender) != 0)
            return;

        bool success = false;
        try
        {
            int frameNo = Volatile.Read(ref _openXrPendingFrameNumber);
            using (var predictedPoseSample = RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.CollectVisible.ApplyPredictedVrRigPose"))
            {
                RuntimeEngine.VRState.InvokeRecalcMatrixOnDraw(RuntimeVrPoseTiming.Predicted);
            }

            if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
            {
                double msSinceLocate = MsSince(_openXrPrepareTimestamp);
                Debug.Out($"OpenXR[{frameNo}] CollectVisible: begin dt(Locate={msSinceLocate:F1}ms)");
            }

            _openXrCollectTimestamp = Stopwatch.GetTimestamp();

            var sourceViewport = TryGetSourceViewport(out XRCamera? sourceCamera, out _);

            // OpenXR eye rendering is strictly driven by the scene VR rig. If the rig has not published
            // HMD-owned eye cameras, skip the frame instead of falling back to the desktop/editor camera.
            var vrInfo = RuntimeEngine.VRState.ViewInformation;
            float nearPlane = sourceCamera?.Parameters.NearZ
                              ?? vrInfo.LeftEyeCamera?.Parameters.NearZ
                              ?? vrInfo.RightEyeCamera?.Parameters.NearZ
                              ?? _openXrFrameBaseCamera?.Parameters.NearZ
                              ?? 0.1f;
            float farPlane = sourceCamera?.Parameters.FarZ
                             ?? vrInfo.LeftEyeCamera?.Parameters.FarZ
                             ?? vrInfo.RightEyeCamera?.Parameters.FarZ
                             ?? _openXrFrameBaseCamera?.Parameters.FarZ
                             ?? 100000.0f;

            if (vrInfo.HMDNode is null || vrInfo.LeftEyeCamera is null || vrInfo.RightEyeCamera is null)
            {
                if (RuntimeVrRenderingServices.TryEnsureHeadsetViewInformation(vrInfo.World, vrInfo.HMDNode, nearPlane, farPlane))
                    vrInfo = RuntimeEngine.VRState.ViewInformation;
            }

            bool rigResolved = TryResolveRequiredOpenXrVrRig(
                out XRCamera? leftEyeCamera,
                out XRCamera? rightEyeCamera,
                out IRuntimeRenderWorld? world,
                out var rigLocomotionRoot,
                out string rigReason);

            if (!rigResolved &&
                RuntimeVrRenderingServices.TryEnsureHeadsetViewInformation(vrInfo.World, null, nearPlane, farPlane))
            {
                Debug.RenderingEvery(
                    "OpenXR.CollectVisible.RepublishedVrRig",
                    TimeSpan.FromSeconds(2),
                    "[OpenXR] Re-published active VR headset view information after strict rig validation failed: {0}",
                    rigReason);

                rigResolved = TryResolveRequiredOpenXrVrRig(
                    out leftEyeCamera,
                    out rightEyeCamera,
                    out world,
                    out rigLocomotionRoot,
                    out rigReason);
            }

            if (!rigResolved)
            {
                Debug.RenderingWarningEvery(
                    "OpenXR.CollectVisible.NoRequiredVrRig",
                    TimeSpan.FromSeconds(1),
                    "[OpenXR] CollectVisible skipped: {0}. No fallback eye cameras or editor roots are used.",
                    rigReason);
                return;
            }

            var baseCamera = leftEyeCamera ?? rightEyeCamera;
            if (baseCamera is null)
            {
                Debug.RenderingWarningEvery(
                    "OpenXR.CollectVisible.NoRequiredBaseCamera",
                    TimeSpan.FromSeconds(1),
                    "[OpenXR] CollectVisible skipped: strict VR rig resolved without a usable camera.");
                return;
            }

            IRuntimeRenderWorld resolvedWorld = world!;
            _openXrFrameBaseCamera = baseCamera;
            _openXrFrameWorld = resolvedWorld;

            // IMPORTANT: RuntimeEngine.Rendering.State.RenderingWorld (and various pipeline passes) resolve the active
            // world through RenderState.WindowViewport.World. When we pass worldOverride into CollectVisible/Render,
            // the viewport's World property still needs to return the same world or lighting can be skipped.
            _openXrLeftViewport?.WorldInstanceOverride = _openXrFrameWorld;
            _openXrRightViewport?.WorldInstanceOverride = _openXrFrameWorld;

            // Locomotion root maps OpenXR tracking space into the engine world. It must come from the
            // HMD rig; null means the HMD itself is rooted at world origin.
            _openXrLocomotionRoot = rigLocomotionRoot;

            if (!EnsureOpenXrEyeCameras(_openXrFrameBaseCamera))
                return;

            if (!TryResolveOpenXrViewRenderModeForCurrentBackend(out VrViewRenderModeResolution viewModeResolution))
                return;

            ApplyOpenXrEyeCameraRenderSettings(
                _openXrLeftEyeCamera!,
                _openXrRightEyeCamera!,
                viewModeResolution);

            bool useTrueSinglePassStereo =
                viewModeResolution.EffectiveImplementationPath == EVrViewRenderImplementationPath.TrueSinglePassStereo;
            if (useTrueSinglePassStereo)
                ReleaseOpenXrExternalEyeViewportPipelinesForTrueStereo();
            else
                ReleaseOpenXrStereoViewportPipelineForExternalEyes();

            EnsureOpenXrViewports(
                GetOpenXrSwapchainWidth(0),
                GetOpenXrSwapchainHeight(0),
                GetOpenXrSwapchainWidth(1),
                GetOpenXrSwapchainHeight(1));

            // OpenXR must not share render pipeline *instances* with the desktop viewport.
            // But it still needs a matching pipeline type/config to avoid missing lighting/post steps.
            var sourcePipeline = sourceViewport?.RenderPipeline ?? baseCamera.RenderPipeline;

            XRViewport collectViewport = _openXrLeftViewport!;
            RenderPipeline collectPipeline;
            RenderCommandCollection collectMeshCommands;

            if (useTrueSinglePassStereo)
            {
                EnsureOpenXrStereoViewport(GetOpenXrSwapchainWidth(0), GetOpenXrSwapchainHeight(0));
                if (_openXrStereoViewport is null)
                {
                    Debug.RenderingWarningEvery(
                        "OpenXR.CollectVisible.NoStereoViewport",
                        TimeSpan.FromSeconds(1),
                        "[OpenXR] CollectVisible skipped: true single-pass stereo was selected, but no stereo viewport is available.");
                    return;
                }

                collectViewport = _openXrStereoViewport;
                collectPipeline = GetOrCreateOpenXrStereoPipeline(sourcePipeline);
                collectViewport.WorldInstanceOverride = resolvedWorld;
                collectViewport.Camera = _openXrLeftEyeCamera;
                collectViewport.MeshRenderCommandsOverride = null;
                if (!ReferenceEquals(collectViewport.RenderPipeline, collectPipeline))
                    collectViewport.RenderPipeline = collectPipeline;

                collectMeshCommands = collectViewport.RenderPipelineInstance.MeshRenderCommands;
                collectMeshCommands.SetOwnerPipeline(collectViewport.RenderPipelineInstance);
                _openXrLeftViewport!.MeshRenderCommandsOverride = null;
                _openXrRightViewport!.MeshRenderCommandsOverride = null;
            }
            else
            {
                RenderPipeline desiredPipeline = GetOrCreateOpenXrPipeline(sourcePipeline);
                collectPipeline = desiredPipeline;
                if (!ReferenceEquals(_openXrLeftViewport!.RenderPipeline, desiredPipeline))
                    _openXrLeftViewport.RenderPipeline = desiredPipeline;
                if (!ReferenceEquals(_openXrRightViewport!.RenderPipeline, desiredPipeline))
                    _openXrRightViewport.RenderPipeline = desiredPipeline;

                RenderCommandCollection sharedMeshCommands = EnsureOpenXrSharedMeshRenderCommands(desiredPipeline);
                sharedMeshCommands.SetOwnerPipeline(_openXrLeftViewport.RenderPipelineInstance);
                _openXrLeftViewport.MeshRenderCommandsOverride = sharedMeshCommands;
                _openXrRightViewport.MeshRenderCommandsOverride = sharedMeshCommands;
                collectMeshCommands = sharedMeshCommands;
            }

            Volatile.Write(ref _pendingXrFrameUsesTrueSinglePassStereo, useTrueSinglePassStereo ? 1 : 0);

            _openXrLeftEyeCamera!.RenderPipeline = collectPipeline;
            _openXrRightEyeCamera!.RenderPipeline = collectPipeline;

            float leftFrustumPadding = UpdateOpenXrEyeCameraFromView(_openXrLeftEyeCamera!, 0);
            float rightFrustumPadding = UpdateOpenXrEyeCameraFromView(_openXrRightEyeCamera!, 1);
            RuntimeEngine.Rendering.Stats.Vr.RecordVrXrCollectFrustumExpansionDegrees(Math.Max(leftFrustumPadding, rightFrustumPadding));

            // NOTE: Do not call World.Lights.UpdateCameraLightIntersections() for the OpenXR eye cameras.
            // That data is stored on the light components (not scoped per camera), so updating it here can
            // cause the desktop view's forward lighting/shadow culling to flicker while OpenXR is active.

            int leftAdded = 0;
            int rightAdded = 0;
            long leftBuildTicks = 0;
            long rightBuildTicks = 0;

            if (!CollectOpenXrStereoVisible(
                    resolvedWorld,
                    _openXrLeftEyeCamera,
                    _openXrRightEyeCamera,
                    collectViewport,
                    collectMeshCommands,
                    out int collectedAdded,
                    out long collectedBuildTicks))
            {
                return;
            }

            leftAdded = collectedAdded;
            rightAdded = collectedAdded;
            leftBuildTicks = collectedBuildTicks;
            rightBuildTicks = collectedBuildTicks;

            RuntimeEngine.Rendering.Stats.Vr.RecordVrPerViewVisibleCounts(
                (uint)Math.Max(0, leftAdded),
                (uint)Math.Max(0, rightAdded));
            RuntimeEngine.Rendering.Stats.Vr.RecordVrPerViewDrawCounts(
                (uint)Math.Max(0, leftAdded),
                (uint)Math.Max(0, rightAdded));
            RuntimeEngine.Rendering.Stats.Vr.RecordVrCommandBuildTimes(
                TimeSpan.FromSeconds(leftBuildTicks / (double)Stopwatch.Frequency),
                TimeSpan.FromSeconds(rightBuildTicks / (double)Stopwatch.Frequency));

            int dbg = OpenXrDebugLifecycle ? Interlocked.Increment(ref _openXrDebugFrameIndex) : 0;
            if (OpenXrDebugLifecycle && (dbg == 1 || (dbg % OpenXrDebugLogEveryNFrames) == 0))
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
            if (!success)
                Volatile.Write(ref _pendingXrFrameUsesTrueSinglePassStereo, 0);
            Volatile.Write(ref _pendingXrFrameCollected, success ? 1 : 0);
        }
    }

    private bool CollectOpenXrStereoVisible(
        IRuntimeRenderWorld world,
        XRCamera leftCamera,
        XRCamera rightCamera,
        XRViewport collectViewport,
        RenderCommandCollection meshCommands,
        out int commandsAdded,
        out long buildTicks)
    {
        commandsAdded = 0;
        buildTicks = 0;

        var hmdNode = RuntimeEngine.VRState.ViewInformation.HMDNode;
        if (hmdNode is null)
        {
            Debug.RenderingWarningEvery(
                "OpenXR.CollectVisible.NoHmdForStereoCull",
                TimeSpan.FromSeconds(1),
                "[OpenXR] CollectVisible skipped: no HMD node is available for combined stereo culling.");
            return false;
        }

        if (collectViewport is null)
        {
            Debug.RenderingWarningEvery(
                "OpenXR.CollectVisible.NoViewportForStereoCull",
                TimeSpan.FromSeconds(1),
                "[OpenXR] CollectVisible skipped: no viewport is available for combined stereo culling.");
            return false;
        }

        _openXrStereoCullProjections[0] = leftCamera.ProjectionMatrix;
        _openXrStereoCullProjections[1] = rightCamera.ProjectionMatrix;
        _openXrStereoCullViews[0] = leftCamera.Transform.InverseLocalMatrix;
        _openXrStereoCullViews[1] = rightCamera.Transform.InverseLocalMatrix;

        Matrix4x4 combinedProjection;
        try
        {
            combinedProjection = ProjectionMatrixCombiner.CombineProjectionMatrices(
                _openXrStereoCullProjections,
                _openXrStereoCullViews);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex, "Failed to combine OpenXR stereo culling frusta.");
            return false;
        }

        _openXrCombinedProjectionMatrix = combinedProjection;
        if (!Matrix4x4.Invert(combinedProjection, out Matrix4x4 inverseCombinedProjection))
        {
            Debug.RenderingWarningEvery(
                "OpenXR.CollectVisible.InvalidCombinedStereoProjection",
                TimeSpan.FromSeconds(1),
                "[OpenXR] CollectVisible skipped: combined stereo culling projection was not invertible.");
            return false;
        }

        Frustum combinedLocalFrustum = new(inverseCombinedProjection);
        IVolume combinedWorldFrustum = combinedLocalFrustum.TransformedBy(hmdNode.Transform.RenderMatrix);

        long started = Stopwatch.GetTimestamp();
        collectViewport.CollectVisible(
            collectMirrors: true,
            worldOverride: world,
            cameraOverride: leftCamera,
            renderCommandsOverride: meshCommands,
            allowScreenSpaceUICollectVisible: false,
            collectionVolumeOverride: combinedWorldFrustum);
        buildTicks = Stopwatch.GetTimestamp() - started;
        commandsAdded = meshCommands.GetCommandsAddedCount();

        return true;
    }

    private void ApplyOpenXrEyeCameraRenderSettings(
        XRCamera leftEyeCamera,
        XRCamera rightEyeCamera,
        VrViewRenderModeResolution viewModeResolution)
    {
        XRCamera sourceCamera = ReferenceEquals(_openXrEyeSettingsSourceCamera, rightEyeCamera)
            ? rightEyeCamera
            : leftEyeCamera;
        XRCamera secondaryCamera = ReferenceEquals(sourceCamera, leftEyeCamera)
            ? rightEyeCamera
            : leftEyeCamera;

        EAntiAliasingMode? sourceAntiAliasingOverride =
            sourceCamera.AntiAliasingModeOverride
            ?? secondaryCamera.AntiAliasingModeOverride;
        EAntiAliasingMode sourceAntiAliasingMode =
            sourceAntiAliasingOverride ?? RuntimeRenderingHostServices.Current.DefaultAntiAliasingMode;
        EAntiAliasingMode antiAliasingMode = ResolveOpenXrEyeAntiAliasingMode(
            sourceAntiAliasingMode,
            viewModeResolution);

        EAntiAliasingMode? antiAliasingOverride =
            sourceAntiAliasingOverride.HasValue || antiAliasingMode != sourceAntiAliasingMode
                ? antiAliasingMode
                : null;
        uint? msaaSampleCountOverride = antiAliasingOverride is EAntiAliasingMode.Msaa
            ? sourceCamera.MsaaSampleCountOverride ?? secondaryCamera.MsaaSampleCountOverride
            : null;
        float? tsrRenderScaleOverride = antiAliasingOverride is EAntiAliasingMode.Tsr
            ? sourceCamera.TsrRenderScaleOverride ?? secondaryCamera.TsrRenderScaleOverride
            : null;
        bool? outputHdrOverride = sourceCamera.OutputHDROverride;

        try
        {
            Volatile.Write(ref _openXrSyncingEyeCameraSettings, 1);
            ApplyOpenXrEyeCameraRenderSettings(leftEyeCamera, antiAliasingOverride, msaaSampleCountOverride, outputHdrOverride, tsrRenderScaleOverride);
            ApplyOpenXrEyeCameraRenderSettings(rightEyeCamera, antiAliasingOverride, msaaSampleCountOverride, outputHdrOverride, tsrRenderScaleOverride);
        }
        finally
        {
            Volatile.Write(ref _openXrSyncingEyeCameraSettings, 0);
        }
    }

    private static void ApplyOpenXrEyeCameraRenderSettings(
        XRCamera eyeCamera,
        EAntiAliasingMode? antiAliasingMode,
        uint? msaaSampleCountOverride,
        bool? outputHdrOverride,
        float? tsrRenderScaleOverride)
    {
        eyeCamera.AntiAliasingModeOverride = antiAliasingMode;
        eyeCamera.MsaaSampleCountOverride = antiAliasingMode is EAntiAliasingMode.Msaa
            ? msaaSampleCountOverride
            : null;
        eyeCamera.OutputHDROverride = outputHdrOverride;
        eyeCamera.TsrRenderScaleOverride = antiAliasingMode is EAntiAliasingMode.Tsr
            ? tsrRenderScaleOverride
            : null;
    }

    private static EAntiAliasingMode ResolveOpenXrEyeAntiAliasingMode(
        EAntiAliasingMode sourceAntiAliasingMode,
        VrViewRenderModeResolution viewModeResolution)
    {
        if (IsOpenXrTemporalHistoryPolicyDisabled(viewModeResolution.TemporalHistoryPolicy) &&
            IsHistoryBasedOpenXrAntiAliasingMode(sourceAntiAliasingMode))
        {
            return EAntiAliasingMode.None;
        }

        return sourceAntiAliasingMode;
    }

    private static bool IsHistoryBasedOpenXrAntiAliasingMode(EAntiAliasingMode mode)
        => mode is EAntiAliasingMode.Taa
            or EAntiAliasingMode.Tsr
            or EAntiAliasingMode.Dlaa;

    private static bool IsOpenXrTemporalHistoryPolicyDisabled(EVrTemporalHistoryPolicy policy)
        => policy is EVrTemporalHistoryPolicy.Disabled
            or EVrTemporalHistoryPolicy.DisabledPerEyeSwapchain
            or EVrTemporalHistoryPolicy.DisabledExternalPerEyeSwapchain;

    private void ReleaseOpenXrExternalEyeViewportPipelinesForTrueStereo()
    {
        ReleaseOpenXrExternalEyeViewportPipelineForTrueStereo(_openXrLeftViewport, 0);
        ReleaseOpenXrExternalEyeViewportPipelineForTrueStereo(_openXrRightViewport, 1);
    }

    private static void ReleaseOpenXrExternalEyeViewportPipelineForTrueStereo(XRViewport? viewport, int eyeIndex)
    {
        if (viewport is null)
            return;

        viewport.MeshRenderCommandsOverride = null;
        if (viewport.RenderPipelineInstance.AssignedPipeline is null)
            return;

        string pipelineName = viewport.RenderPipelineInstance.DebugName;
        viewport.RenderPipeline = null;

        Debug.RenderingEvery(
            $"OpenXR.TrueStereo.ReleaseExternalEyePipeline.{eyeIndex}",
            TimeSpan.FromSeconds(1),
            "[OpenXR] Released external per-eye pipeline '{0}' for eye {1} because true single-pass stereo owns VR rendering resources this frame.",
            pipelineName,
            eyeIndex);
    }

    private void ReleaseOpenXrStereoViewportPipelineForExternalEyes()
    {
        XRViewport? viewport = _openXrStereoViewport;
        if (viewport is null)
            return;

        viewport.MeshRenderCommandsOverride = null;
        if (viewport.RenderPipelineInstance.AssignedPipeline is null)
            return;

        string pipelineName = viewport.RenderPipelineInstance.DebugName;
        viewport.RenderPipeline = null;

        Debug.RenderingEvery(
            $"OpenXR.ExternalEyes.ReleaseStereoPipeline.{GetHashCode()}",
            TimeSpan.FromSeconds(1),
            "[OpenXR] Released true single-pass stereo pipeline '{0}' because external per-eye rendering owns VR resources this frame.",
            pipelineName);
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
        IRuntimeRenderWorld world,
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
                IRuntimeRenderWorld? world;
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

        if (Volatile.Read(ref _pendingXrFrameUsesTrueSinglePassStereo) != 0)
        {
            XRViewport? stereoViewport = _openXrStereoViewport;
            RenderCommandCollection? stereoMeshCommands = stereoViewport?.RenderPipelineInstance.MeshRenderCommands;
            if (stereoViewport is null || stereoMeshCommands is null)
            {
                Debug.RenderingWarningEvery(
                    "OpenXR.SwapBuffers.NoStereoMeshCommands",
                    TimeSpan.FromSeconds(1),
                    "[OpenXR] SwapBuffers skipped: true single-pass stereo command collection was not created.");
                return;
            }

            int updatingCount = stereoMeshCommands.GetUpdatingCommandCount();
            stereoViewport.SwapBuffers(stereoMeshCommands, allowScreenSpaceUISwap: false);
            Debug.RenderingEvery(
                $"OpenXR.SwapBuffers.TrueStereoCommands.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] SwapBuffers published true single-pass stereo commands. updating={0} rendering={1}",
                updatingCount,
                stereoMeshCommands.GetRenderingCommandCount());
        }
        else if (_openXrSharedMeshRenderCommands is null)
        {
            Debug.RenderingWarningEvery(
                "OpenXR.SwapBuffers.NoSharedMeshCommands",
                TimeSpan.FromSeconds(1),
                "[OpenXR] SwapBuffers skipped: combined stereo command collection was not created.");
            return;
        }
        else
        {
            _openXrSharedMeshRenderCommands.SwapBuffers();
        }

        Interlocked.Exchange(ref _framePrepared, 1);

        if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
            Debug.Out($"OpenXR[{frameNo}] SwapBuffers: framePrepared=1");
    }

    /// <summary>
    /// Pacing-owner helper.
    /// Waits/begins an OpenXR frame and locates views, making that frame available for CollectVisible.
    /// </summary>
    private void PrepareNextFrameForPacingOwner()
    {
        // Called by the configured pacing owner. Prepares the next OpenXR frame
        // (WaitFrame/BeginFrame/LocateViews) so the CollectVisible thread can build buffers for it.
        if (!_sessionBegun)
            return;

        if (Interlocked.CompareExchange(ref _openXrFramePrepActive, 1, 0) != 0)
            return;

        try
        {
            // Only one OpenXR frame can be "in flight" between BeginFrame and EndFrame.
            if (Volatile.Read(ref _pendingXrFrame) != 0)
                return;

            // Clear any stale publish flags.
            Volatile.Write(ref _framePrepared, 0);
            Volatile.Write(ref _pendingXrFrameCollected, 0);
            Volatile.Write(ref _pendingXrFrameUsesTrueSinglePassStereo, 0);

            if (!WaitFrame(out _frameState))
                return;

            if (!_sessionBegun)
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

            long relocateTicks = 0;
            if (OpenXrCollectPosePolicy == OpenXrCollectVisiblePosePolicy.RelocatePredicted)
            {
                long relocateStart = Stopwatch.GetTimestamp();
                if (!LocateViews(OpenXrPoseTiming.Predicted))
                {
                    EndBegunFrameWithoutLayers(frameNo, "Relocate predicted LocateViews failed");
                    return;
                }
                relocateTicks = Stopwatch.GetTimestamp() - relocateStart;
            }

            RuntimeEngine.Rendering.Stats.Vr.RecordVrXrRelocatePredictedTime(
                TimeSpan.FromSeconds(relocateTicks / (double)Stopwatch.Frequency));

            try
            {
                // Predicted controller/tracker poses for the upcoming frame.
                UpdateActionPoseCaches(OpenXrPoseTiming.Predicted);

                // The CollectVisible thread consumes this frame's predicted views and applies the
                // app-visible predicted rig pose while it owns the visibility build. Do not publish
                // that pose here: in DedicatedThread mode this method runs on the XR pacing thread
                // immediately after xrEndFrame, while the render thread may still be drawing the
                // independent desktop view.
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
        finally
        {
            Volatile.Write(ref _openXrFramePrepActive, 0);
        }
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

            var endResult = EndFrameWithTiming(in frameEndInfoNoLayers);
            if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
                Debug.Out($"OpenXR[{frameNo}] Prepare: aborted frame ({reason}), EndFrame(no layers) => {endResult}");
        }
        finally
        {
            Volatile.Write(ref _framePrepared, 0);
            Volatile.Write(ref _frameSkipRender, 0);
            Volatile.Write(ref _pendingXrFrame, 0);
            Volatile.Write(ref _pendingXrFrameCollected, 0);
            // Even an aborted prep still consumes the pacing-thread's wake; signal so it can retry next cycle.
            SignalPacingThreadFrameSubmitted();
        }
    }
}
