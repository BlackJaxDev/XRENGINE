using System.Diagnostics;
using ImageMagick;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Vulkan;

namespace XREngine.Editor.Mcp;

internal sealed partial class ViewportSequenceCaptureSession
{
    private void OnPostRenderViewports()
    {
        AbstractRenderer? renderer = AbstractRenderer.Current ?? (_window.IsDisposed ? null : _window.Renderer);
        if (renderer is null)
        {
            RequestStop(
                ViewportSequenceCaptureStopReason.RendererUnavailable,
                ViewportSequenceCaptureState.Failed,
                "No renderer was available for the target window.");
            return;
        }

        BoundingRectangle captureRegion = ResolveCaptureRegion(_viewport);
        if (captureRegion.Width <= 0 || captureRegion.Height <= 0)
        {
            RequestStop(
                ViewportSequenceCaptureStopReason.CaptureError,
                ViewportSequenceCaptureState.Failed,
                $"The target viewport has an invalid capture region of {captureRegion.Width}x{captureRegion.Height}.");
            return;
        }

        long nowTimestamp = Stopwatch.GetTimestamp();
        double elapsedSeconds = Stopwatch.GetElapsedTime(_startTimestamp, nowTimestamp).TotalSeconds;
        ulong renderFrameId = Engine.Rendering.State.RenderFrameId;
        int outputWidth = ScaleDimension(captureRegion.Width, _options.OutputScale);
        int outputHeight = ScaleDimension(captureRegion.Height, _options.OutputScale);
        long outputPixels = (long)outputWidth * outputHeight;
        long readbackBytes = checked((long)captureRegion.Width * captureRegion.Height * 4L);

        ViewportSequenceCaptureFrame? frame = null;
        bool stopAfterSchedule = false;
        ViewportSequenceCaptureStopReason deferredStopReason = ViewportSequenceCaptureStopReason.None;
        string? deferredFailure = null;

        lock (_sync)
        {
            if (_state != ViewportSequenceCaptureState.Capturing)
                return;

            if (_options.DurationSeconds is double durationSeconds && elapsedSeconds >= durationSeconds)
            {
                deferredStopReason = ViewportSequenceCaptureStopReason.DurationElapsed;
            }
            else if (renderFrameId == _lastObservedRenderFrameId)
            {
                return;
            }
            else
            {
                _lastObservedRenderFrameId = renderFrameId;
                long observedIndex = _observedRenderFrameCount++;
                if (observedIndex % _options.FrameStride != 0)
                    return;

                if (_options.CaptureFramesPerSecond is double captureFramesPerSecond &&
                    elapsedSeconds - _lastScheduledElapsedSeconds < 1.0 / captureFramesPerSecond)
                    return;

                if (_scheduledFrameCount >= _options.CaptureLimit)
                {
                    deferredStopReason = _options.FrameCount.HasValue
                        ? ViewportSequenceCaptureStopReason.FrameCountReached
                        : ViewportSequenceCaptureStopReason.MaxFramesReached;
                }
                else if (_scheduledOutputPixels + outputPixels > ViewportSequenceCaptureOptions.MaximumTotalOutputPixels)
                {
                    deferredStopReason = ViewportSequenceCaptureStopReason.PixelBudgetExceeded;
                    deferredFailure = $"Capturing this frame would exceed the {ViewportSequenceCaptureOptions.MaximumTotalOutputPixels:N0}-pixel session output budget.";
                }
                else if (_inFlightReadbacks >= _options.MaxInFlightReadbacks ||
                         _inFlightReadbackBytes + readbackBytes > ViewportSequenceCaptureOptions.MaximumInFlightReadbackBytes)
                {
                    string reason = _inFlightReadbacks >= _options.MaxInFlightReadbacks
                        ? $"The bounded readback queue already contains {_inFlightReadbacks} frame(s)."
                        : $"The bounded readback queue would exceed {ViewportSequenceCaptureOptions.MaximumInFlightReadbackBytes / (1024 * 1024)} MiB.";

                    if (_options.OverflowPolicy == ViewportSequenceCaptureOverflowPolicy.Drop)
                    {
                        _droppedFrames.Add(new ViewportSequenceDroppedFrame
                        {
                            RenderFrameId = renderFrameId,
                            CaptureElapsedSeconds = elapsedSeconds,
                            Reason = reason,
                        });
                        _lastUpdatedAtUtc = DateTimeOffset.UtcNow;
                        return;
                    }

                    deferredStopReason = ViewportSequenceCaptureStopReason.BackpressureExceeded;
                    deferredFailure = $"{reason} Consecutive-frame capture stopped instead of silently dropping a frame. Use overflow_policy='drop' only when gaps are acceptable.";
                }
                else
                {
                    int captureIndex = _scheduledFrameCount;
                    // One bounded metadata record per requested output frame is retained for
                    // polling and the final manifest; the session frame cap bounds this allocation.
                    frame = new ViewportSequenceCaptureFrame
                    {
                        CaptureIndex = captureIndex,
                        RenderFrameId = renderFrameId,
                        ScheduledAtUtc = DateTimeOffset.UtcNow,
                        CaptureElapsedSeconds = elapsedSeconds,
                        RenderDeltaSeconds = Engine.Time.Timer.Render.Delta,
                        SourceX = captureRegion.X,
                        SourceY = captureRegion.Y,
                        SourceWidth = captureRegion.Width,
                        SourceHeight = captureRegion.Height,
                        OutputWidth = outputWidth,
                        OutputHeight = outputHeight,
                        Path = Path.Combine(OutputDirectory, $"frame_{captureIndex:D6}.png"),
                        Backend = _backend,
                        Camera = CaptureCameraPose(),
                    };

                    _frames.Add(frame);
                    _scheduledFrameCount++;
                    _inFlightReadbacks++;
                    _inFlightReadbackBytes += readbackBytes;
                    _scheduledOutputPixels += outputPixels;
                    _lastScheduledElapsedSeconds = elapsedSeconds;
                    _lastUpdatedAtUtc = DateTimeOffset.UtcNow;

                    stopAfterSchedule = _scheduledFrameCount >= _options.CaptureLimit;
                }
            }
        }

        if (deferredStopReason != ViewportSequenceCaptureStopReason.None)
        {
            RequestStop(
                deferredStopReason,
                deferredFailure is null ? ViewportSequenceCaptureState.Completed : ViewportSequenceCaptureState.Failed,
                deferredFailure);
            return;
        }

        if (frame is null)
            return;

        bool queued;
        string? queueFailure;
        try
        {
            queued = BeginRendererCapture(renderer, captureRegion, frame, readbackBytes, out queueFailure);
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref frame.CompletionClaimed, 1) == 0)
                CompleteFrame(frame, readbackBytes, $"Failed to schedule render readback: {ex.Message}");
            return;
        }

        if (!queued)
        {
            HandleRendererQueueRejection(
                frame,
                readbackBytes,
                outputPixels,
                queueFailure ?? "The renderer rejected the screenshot readback request.");
            return;
        }

        if (stopAfterSchedule)
        {
            RequestStop(
                _options.FrameCount.HasValue
                    ? ViewportSequenceCaptureStopReason.FrameCountReached
                    : ViewportSequenceCaptureStopReason.MaxFramesReached,
                ViewportSequenceCaptureState.Completed,
                error: null);
        }
    }

    private bool BeginRendererCapture(
        AbstractRenderer renderer,
        BoundingRectangle captureRegion,
        ViewportSequenceCaptureFrame frame,
        long readbackBytes,
        out string? queueFailure)
    {
        using IDisposable? pipelineReadbackScope = _viewport.EnterRenderPipelineReadbackScope();
        using IDisposable? targetReadScope = _viewport.LastRenderedTargetFBO?.BindForReadingState();
        if (targetReadScope is not null)
            renderer.SetReadBuffer(EReadBufferMode.ColorAttachment0);
        else
            renderer.BindFrameBuffer(EFramebufferTarget.ReadFramebuffer, null);

        return renderer.TryQueueScreenshotReadback(captureRegion, _options.PreserveAlpha, result =>
        {
            if (Interlocked.Exchange(ref frame.CompletionClaimed, 1) != 0)
            {
                result.Image?.Dispose();
                return;
            }

            frame.ReadbackRawByteCount = result.RawByteCount;
            frame.ReadbackGpuCompletionSeconds = result.GpuCompletionSeconds;
            frame.ReadbackCpuProcessingSeconds = result.CpuProcessingSeconds;
            frame.ReadbackQueueSlot = result.QueueSlot;
            frame.ReadbackSourceFormat = result.SourceFormat;
            frame.UsedMultisampleResolve = result.UsedMultisampleResolve;

            if (!result.Succeeded || result.Image is null)
            {
                CompleteFrame(
                    frame,
                    readbackBytes,
                    result.Error ?? "The renderer returned a null screenshot image.");
                return;
            }

            // Diagnostic captures may allocate and encode, but neither operation is permitted
            // to run in the per-frame render callback. The in-flight queue bounds this work.
            _ = Task.Run(() => ProcessCapturedImage(renderer, result.Image, frame, readbackBytes));
        }, out queueFailure);
    }

    private void HandleRendererQueueRejection(
        ViewportSequenceCaptureFrame frame,
        long readbackBytes,
        long outputPixels,
        string reason)
    {
        if (Interlocked.Exchange(ref frame.CompletionClaimed, 1) != 0)
            return;

        if (_options.OverflowPolicy != ViewportSequenceCaptureOverflowPolicy.Drop)
        {
            CompleteFrame(frame, readbackBytes, reason);
            return;
        }

        lock (_sync)
        {
            if (_frames.Remove(frame))
            {
                _scheduledFrameCount = Math.Max(0, _scheduledFrameCount - 1);
                _inFlightReadbacks = Math.Max(0, _inFlightReadbacks - 1);
                _inFlightReadbackBytes = Math.Max(0L, _inFlightReadbackBytes - readbackBytes);
                _scheduledOutputPixels = Math.Max(0L, _scheduledOutputPixels - outputPixels);
            }

            _droppedFrames.Add(new ViewportSequenceDroppedFrame
            {
                RenderFrameId = frame.RenderFrameId,
                CaptureElapsedSeconds = frame.CaptureElapsedSeconds,
                Reason = $"Renderer readback queue rejected the frame: {reason}",
            });
            _lastUpdatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private void ProcessCapturedImage(
        AbstractRenderer renderer,
        MagickImage image,
        ViewportSequenceCaptureFrame frame,
        long readbackBytes)
    {
        try
        {
            using (image)
            {
                if (renderer.ScreenshotRequiresVerticalFlip)
                    image.Flip();

                if (image.Width != (uint)frame.OutputWidth || image.Height != (uint)frame.OutputHeight)
                    image.Resize((uint)frame.OutputWidth, (uint)frame.OutputHeight);

                image.Strip();
                image.Write(frame.Path, MagickFormat.Png);
                CompleteFrame(frame, readbackBytes, error: null);
            }
        }
        catch (Exception ex)
        {
            CompleteFrame(frame, readbackBytes, $"Failed to encode frame {frame.CaptureIndex}: {ex.Message}");
        }
    }

    private void CompleteFrame(ViewportSequenceCaptureFrame frame, long readbackBytes, string? error)
    {
        bool failActiveSession = false;
        bool beginFinalization = false;

        lock (_sync)
        {
            frame.CompletedAtUtc = DateTimeOffset.UtcNow;
            frame.CompletionElapsedSeconds = Stopwatch.GetElapsedTime(_startTimestamp).TotalSeconds;
            frame.Error = error;
            _inFlightReadbacks = Math.Max(0, _inFlightReadbacks - 1);
            _inFlightReadbackBytes = Math.Max(0L, _inFlightReadbackBytes - readbackBytes);
            _lastUpdatedAtUtc = DateTimeOffset.UtcNow;

            if (error is null)
            {
                _completedFrameCount++;
            }
            else
            {
                _failedFrameCount++;
                if (_state == ViewportSequenceCaptureState.Capturing)
                {
                    failActiveSession = true;
                }
                else if (_terminalOutcome == ViewportSequenceCaptureState.Completed)
                {
                    _terminalOutcome = ViewportSequenceCaptureState.Failed;
                    _stopReason = ViewportSequenceCaptureStopReason.CaptureError;
                    _error ??= error;
                }
                else
                {
                    _warnings.Add(error);
                }
            }

            beginFinalization = _state == ViewportSequenceCaptureState.Stopping && _inFlightReadbacks == 0;
        }

        if (failActiveSession)
        {
            RequestStop(
                ViewportSequenceCaptureStopReason.CaptureError,
                ViewportSequenceCaptureState.Failed,
                error);
            return;
        }

        if (beginFinalization)
            BeginFinalization();
    }

    private void RequestStop(
        ViewportSequenceCaptureStopReason reason,
        ViewportSequenceCaptureState terminalOutcome,
        string? error)
    {
        bool detach = false;
        bool beginFinalization = false;

        lock (_sync)
        {
            if (_state is ViewportSequenceCaptureState.Finalizing || IsTerminalState(_state))
                return;

            if (_state == ViewportSequenceCaptureState.Capturing)
            {
                _state = ViewportSequenceCaptureState.Stopping;
                _stopReason = reason;
                _terminalOutcome = terminalOutcome;
                _error = error;
                detach = true;
            }
            else if (_state == ViewportSequenceCaptureState.Stopping)
            {
                if (_terminalOutcome == ViewportSequenceCaptureState.Completed && terminalOutcome != ViewportSequenceCaptureState.Completed)
                {
                    _terminalOutcome = terminalOutcome;
                    _stopReason = reason;
                    _error = error;
                }
                else if (!string.IsNullOrWhiteSpace(error))
                {
                    _warnings.Add(error);
                }
            }

            _lastUpdatedAtUtc = DateTimeOffset.UtcNow;
            beginFinalization = _state == ViewportSequenceCaptureState.Stopping && _inFlightReadbacks == 0;
        }

        if (detach)
            Detach();

        if (beginFinalization)
            BeginFinalization();
    }

    private void Detach()
    {
        if (Interlocked.Exchange(ref _attached, 0) == 0)
            return;

        _durationCancellation.Cancel();
        _window.PostRenderViewportsCallback -= OnPostRenderViewports;
        _window.ClosingRequested -= OnWindowClosing;
    }
}
