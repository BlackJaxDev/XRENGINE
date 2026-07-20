using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using XREngine.Rendering;

namespace XREngine.Editor.Mcp;

internal sealed partial class ViewportSequenceCaptureSession
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private void BeginFinalization()
    {
        if (Interlocked.Exchange(ref _finalizationStarted, 1) != 0)
            return;

        lock (_sync)
        {
            if (_state != ViewportSequenceCaptureState.Stopping || _inFlightReadbacks != 0)
            {
                Interlocked.Exchange(ref _finalizationStarted, 0);
                return;
            }

            _state = ViewportSequenceCaptureState.Finalizing;
            _lastUpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        _ = Task.Run(FinalizeArtifacts);
    }

    private void FinalizeArtifacts()
    {
        ViewportSequenceCaptureFrame[] finalizedFrames;
        ViewportSequenceCaptureState desiredOutcome;
        string? finalizedContactSheetPath = null;

        lock (_sync)
        {
            finalizedFrames = _frames.Select(static frame => frame.Clone()).ToArray();
            desiredOutcome = _terminalOutcome;
        }

        string? finalizationError = null;
        try
        {
            ViewportSequenceCaptureFrame[] successfulFrames = finalizedFrames
                .Where(static frame => frame.Succeeded && File.Exists(frame.Path))
                .OrderBy(static frame => frame.CaptureIndex)
                .ToArray();

            if (desiredOutcome == ViewportSequenceCaptureState.Completed && successfulFrames.Length == 0)
                throw new InvalidOperationException("The capture interval ended without producing any frames.");

            if (_options.ComputeFrameDifferences && successfulFrames.Length > 0)
                ViewportSequenceCaptureImageAnalyzer.Analyze(successfulFrames);

            if (_options.CreateContactSheet && successfulFrames.Length > 0)
            {
                string contactSheetPath = Path.Combine(OutputDirectory, "contact-sheet.png");
                if (!ViewportSequenceCaptureContactSheetWriter.TryWrite(
                        successfulFrames,
                        contactSheetPath,
                        _options.ContactSheetColumns,
                        _options.ContactSheetThumbnailWidth,
                        out string? contactSheetError))
                    throw new InvalidOperationException($"Failed to build the contact sheet: {contactSheetError}");

                finalizedContactSheetPath = contactSheetPath;
            }

            lock (_sync)
            {
                for (int i = 0; i < finalizedFrames.Length; i++)
                    ApplyFinalizedMetadata(_frames[i], finalizedFrames[i]);
                _contactSheetPath = finalizedContactSheetPath;
            }
        }
        catch (Exception ex)
        {
            finalizationError = ex.Message;
        }

        ViewportSequenceCaptureSnapshot manifestSnapshot;
        lock (_sync)
        {
            if (finalizationError is not null)
            {
                if (_terminalOutcome == ViewportSequenceCaptureState.Completed)
                {
                    _terminalOutcome = ViewportSequenceCaptureState.Failed;
                    _stopReason = ViewportSequenceCaptureStopReason.FinalizationError;
                    _error = finalizationError;
                }
                else
                {
                    _warnings.Add($"Finalization warning: {finalizationError}");
                }
            }

            _finishedAtUtc = DateTimeOffset.UtcNow;
            _lastUpdatedAtUtc = _finishedAtUtc.Value;
            manifestSnapshot = CreateSnapshotNoLock(includeFrames: true, stateOverride: _terminalOutcome);
        }

        try
        {
            WriteManifest(manifestSnapshot);
            lock (_sync)
            {
                _state = _terminalOutcome;
                _lastUpdatedAtUtc = DateTimeOffset.UtcNow;
            }
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _state = ViewportSequenceCaptureState.Failed;
                _stopReason = ViewportSequenceCaptureStopReason.FinalizationError;
                _error = $"Failed to write capture manifest: {ex.Message}";
                _lastUpdatedAtUtc = DateTimeOffset.UtcNow;
            }
        }
        finally
        {
            _durationCancellation.Dispose();
            _terminalCallback(this);
        }
    }

    private void WriteManifest(ViewportSequenceCaptureSnapshot snapshot)
    {
        string temporaryPath = ManifestPath + ".tmp";
        try
        {
            string json = JsonSerializer.Serialize(snapshot, ManifestJsonOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, ManifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private ViewportSequenceCaptureSnapshot CreateSnapshotNoLock(
        bool includeFrames,
        ViewportSequenceCaptureState? stateOverride = null)
    {
        ViewportSequenceCaptureState effectiveState = stateOverride ?? _state;
        double elapsedSeconds = _finishedAtUtc.HasValue
            ? (_finishedAtUtc.Value - _startedAtUtc).TotalSeconds
            : Stopwatch.GetElapsedTime(_startTimestamp).TotalSeconds;
        double progress = CalculateProgressNoLock(elapsedSeconds, effectiveState);

        return new ViewportSequenceCaptureSnapshot
        {
            CaptureId = CaptureId,
            State = ToWireName(effectiveState),
            Active = !IsTerminalState(effectiveState),
            StopReason = _stopReason == ViewportSequenceCaptureStopReason.None ? null : ToWireName(_stopReason),
            StartedAtUtc = _startedAtUtc,
            FinishedAtUtc = _finishedAtUtc,
            LastUpdatedAtUtc = _lastUpdatedAtUtc,
            ElapsedSeconds = elapsedSeconds,
            Progress = progress,
            CaptureMode = _options.IsDurationBased ? "duration_seconds" : "frame_count",
            RequestedFrameCount = _options.FrameCount,
            DurationSeconds = _options.DurationSeconds,
            FrameStride = _options.FrameStride,
            CaptureFramesPerSecond = _options.CaptureFramesPerSecond,
            MaxFrames = _options.MaxFrames,
            OutputScale = _options.OutputScale,
            MaxInFlightReadbacks = _options.MaxInFlightReadbacks,
            OverflowPolicy = ToWireName(_options.OverflowPolicy),
            PreserveAlpha = _options.PreserveAlpha,
            CreateContactSheet = _options.CreateContactSheet,
            ComputeFrameDifferences = _options.ComputeFrameDifferences,
            ScheduledFrameCount = _scheduledFrameCount,
            CompletedFrameCount = _completedFrameCount,
            FailedFrameCount = _failedFrameCount,
            DroppedFrameCount = _droppedFrames.Count,
            InFlightReadbacks = _inFlightReadbacks,
            ScheduledOutputPixels = _scheduledOutputPixels,
            SourceWidth = _initialSourceWidth,
            SourceHeight = _initialSourceHeight,
            EstimatedOutputWidth = _estimatedOutputWidth,
            EstimatedOutputHeight = _estimatedOutputHeight,
            Backend = _backend,
            RendererReadback = _window.IsDisposed
                ? null
                : _window.Renderer?.GetScreenshotReadbackStatus(),
            WindowIndex = _windowIndex,
            WindowTitle = _windowTitle,
            ViewportIndex = _viewportIndex,
            CameraNodeId = _cameraNodeId,
            CameraNodeName = _cameraNodeName,
            OutputDirectory = OutputDirectory,
            ManifestPath = ManifestPath,
            ContactSheetPath = _contactSheetPath,
            Error = _error,
            Warnings = [.. _warnings],
            FramesIncluded = includeFrames,
            Frames = includeFrames ? _frames.Select(static frame => frame.Clone()).ToArray() : null,
            DroppedFrames = includeFrames
                ? _droppedFrames.Select(static frame => new ViewportSequenceDroppedFrame
                {
                    RenderFrameId = frame.RenderFrameId,
                    CaptureElapsedSeconds = frame.CaptureElapsedSeconds,
                    Reason = frame.Reason,
                }).ToArray()
                : null,
        };
    }

    private double CalculateProgressNoLock(
        double elapsedSeconds,
        ViewportSequenceCaptureState effectiveState)
    {
        if (effectiveState == ViewportSequenceCaptureState.Completed)
            return 1.0;

        if (_options.FrameCount is int frameCount)
            return Math.Clamp(_scheduledFrameCount / (double)frameCount, 0.0, 1.0);

        return Math.Clamp(elapsedSeconds / _options.DurationSeconds!.Value, 0.0, 1.0);
    }

    private ViewportSequenceCaptureCameraPose? CaptureCameraPose()
    {
        XRCamera? camera = _viewport.ActiveCamera;
        if (camera is null)
            return null;

        Vector3 position = camera.Transform.WorldTranslation;
        Quaternion rotation = camera.Transform.WorldRotation;
        Vector3 forward = camera.Transform.WorldForward;
        return new ViewportSequenceCaptureCameraPose
        {
            NodeId = _viewport.CameraComponent?.SceneNode?.ID.ToString("D"),
            NodeName = _viewport.CameraComponent?.SceneNode?.Name,
            PositionX = position.X,
            PositionY = position.Y,
            PositionZ = position.Z,
            RotationX = rotation.X,
            RotationY = rotation.Y,
            RotationZ = rotation.Z,
            RotationW = rotation.W,
            ForwardX = forward.X,
            ForwardY = forward.Y,
            ForwardZ = forward.Z,
        };
    }

    private static void ApplyFinalizedMetadata(
        ViewportSequenceCaptureFrame target,
        ViewportSequenceCaptureFrame source)
    {
        target.ContentSha256 = source.ContentSha256;
        target.MeanLuminance = source.MeanLuminance;
        target.BlackPixelRatio = source.BlackPixelRatio;
        target.DifferenceFromPrevious = source.DifferenceFromPrevious;
        target.ContactSheetRow = source.ContactSheetRow;
        target.ContactSheetColumn = source.ContactSheetColumn;
    }

    private static int ScaleDimension(int dimension, double scale)
        => Math.Max(1, (int)Math.Round(dimension * scale));

    private static bool IsTerminalState(ViewportSequenceCaptureState state)
        => state is ViewportSequenceCaptureState.Completed
            or ViewportSequenceCaptureState.Canceled
            or ViewportSequenceCaptureState.Failed;

    private static string ToWireName(ViewportSequenceCaptureState state)
        => state.ToString().ToLowerInvariant();

    private static string ToWireName(ViewportSequenceCaptureOverflowPolicy policy)
        => policy.ToString().ToLowerInvariant();

    private static string ToWireName(ViewportSequenceCaptureStopReason reason)
        => reason switch
        {
            ViewportSequenceCaptureStopReason.FrameCountReached => "frame_count_reached",
            ViewportSequenceCaptureStopReason.DurationElapsed => "duration_elapsed",
            ViewportSequenceCaptureStopReason.MaxFramesReached => "max_frames_reached",
            ViewportSequenceCaptureStopReason.UserCanceled => "user_canceled",
            ViewportSequenceCaptureStopReason.WindowClosing => "window_closing",
            ViewportSequenceCaptureStopReason.BackpressureExceeded => "backpressure_exceeded",
            ViewportSequenceCaptureStopReason.PixelBudgetExceeded => "pixel_budget_exceeded",
            ViewportSequenceCaptureStopReason.RendererUnavailable => "renderer_unavailable",
            ViewportSequenceCaptureStopReason.UnsupportedRenderer => "unsupported_renderer",
            ViewportSequenceCaptureStopReason.CaptureError => "capture_error",
            ViewportSequenceCaptureStopReason.FinalizationError => "finalization_error",
            _ => "none",
        };
}
