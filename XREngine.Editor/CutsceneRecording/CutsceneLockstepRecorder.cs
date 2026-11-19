using System.Diagnostics;
using XREngine.Editor.Mcp;
using XREngine.Rendering;

namespace XREngine.Editor.CutsceneRecording;

/// <summary>
/// Records the cutscene of a <see cref="CutsceneRecordingScene"/> frame by frame to an MP4 master video, a poster PNG, an
/// optional optimized file size copy, and an optional contact sheet. Each frame shows the exact cutscene state at its time,
/// so the recording does not run in real time. Nothing in the recorder depends on what the cutscene shows.
/// </summary>
internal static class CutsceneLockstepRecorder
{
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SegmentNoProgressTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan SegmentTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Warms up every shot, records all frames, and writes the output files into <paramref name="outputDirectory"/>.
    /// Call it from a worker thread. It runs the scene changes on the update thread.
    /// </summary>
    /// <returns>The paths and timings of the recording.</returns>
    public static async Task<CutsceneRecordingResult> RecordAsync(
        CutsceneRecordingScene scene,
        string outputDirectory,
        string ffmpegPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);

        CutsceneRecordSettings settings = scene.Settings;
        int fps = settings.FramesPerSecond;
        double durationSeconds = scene.GetDurationSeconds();
        if (!(durationSeconds > 0.0))
            throw new InvalidOperationException("The cutscene has no duration. Set a duration on the asset or in the settings.");

        int totalFrames = Math.Clamp((int)Math.Round(durationSeconds * fps), 1, CutsceneRecordSettings.MaximumFrameCount);
        double recordedSeconds = totalFrames / (double)fps;
        int posterFrame = settings.PosterTimeSeconds is { } posterSeconds
            ? Math.Clamp((int)Math.Round(posterSeconds * fps), 0, totalFrames - 1)
            : totalFrames - 1;

        Directory.CreateDirectory(outputDirectory);
        long startTimestamp = Stopwatch.GetTimestamp();
        await WarmUpAsync(scene, totalFrames, cancellationToken).ConfigureAwait(false);
        double warmupSeconds = Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;

        // Session manifests go to one clean folder. Frame pixels go straight to ffmpeg and never touch the disk.
        string captureRootDir = Path.Combine(outputDirectory, "capture");
        CutsceneVideoOutputs.DeleteDirectoryIfPresent(captureRootDir);
        Directory.CreateDirectory(captureRootDir);

        string posterPath = Path.Combine(outputDirectory, settings.PosterFileName);
        string masterVideoPath = Path.Combine(outputDirectory, settings.MasterFileName);
        long captureStartTimestamp = Stopwatch.GetTimestamp();
        CutsceneLockstepController lockstep = new(scene.Viewport, scene, totalFrames);
        int segmentCount = (totalFrames + ViewportSequenceCaptureOptions.MaximumFrames - 1) / ViewportSequenceCaptureOptions.MaximumFrames;
        await using (FfmpegRawVideoPipe videoPipe = new(
            ffmpegPath,
            settings.Width,
            settings.Height,
            fps,
            totalFrames,
            settings.VideoEncoder,
            masterVideoPath,
            posterFrameIndex: posterFrame,
            posterPath))
        {
            // One capture session accepts at most ViewportSequenceCaptureOptions.MaximumFrames frames. The cutscene records
            // in consecutive segments that share one global frame index and one ffmpeg pipe.
            for (int segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
            {
                int segmentStart = segmentIndex * ViewportSequenceCaptureOptions.MaximumFrames;
                int segmentFrames = Math.Min(ViewportSequenceCaptureOptions.MaximumFrames, totalFrames - segmentStart);
                string segmentRootDir = Path.Combine(captureRootDir, $"segment_{segmentIndex:D2}");
                Directory.CreateDirectory(segmentRootDir);

                videoPipe.SegmentOffset = segmentStart;
                ViewportSequenceCaptureSnapshot snapshot = await CaptureSegmentAsync(
                    scene.Viewport,
                    lockstep,
                    videoPipe,
                    segmentStart,
                    segmentFrames,
                    segmentRootDir,
                    cancellationToken).ConfigureAwait(false);

                File.Copy(snapshot.ManifestPath, Path.Combine(outputDirectory, $"manifest-segment-{segmentIndex:D2}.json"), overwrite: true);
            }

            await videoPipe.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        double captureSeconds = Stopwatch.GetElapsedTime(captureStartTimestamp).TotalSeconds;
        Console.WriteLine(
            $"Cutscene capture finished: segments={segmentCount}, frames={totalFrames}, captureSeconds={captureSeconds:F1}, " +
            $"capturedFps={totalFrames / Math.Max(captureSeconds, 0.001):F2}, {lockstep.FormatStatistics()}.");
        CutsceneVideoOutputs.DeleteDirectoryIfPresent(captureRootDir);

        string? optimizedPath = null;
        long optimizedStartTimestamp = Stopwatch.GetTimestamp();
        if (settings.OptimizedFileSizeMaxBytes > 0)
        {
            optimizedPath = Path.Combine(outputDirectory, settings.OptimizedFileName);
            await CutsceneVideoOutputs.EncodeOptimizedFileSizeAsync(
                ffmpegPath,
                settings.VideoEncoder,
                masterVideoPath,
                recordedSeconds,
                settings.OptimizedFileSizeMaxBytes,
                optimizedPath,
                cancellationToken).ConfigureAwait(false);
        }
        double optimizedSeconds = Stopwatch.GetElapsedTime(optimizedStartTimestamp).TotalSeconds;

        string? contactSheetPath = null;
        long contactSheetStartTimestamp = Stopwatch.GetTimestamp();
        if (settings.ContactSheetEnabled)
        {
            contactSheetPath = Path.Combine(outputDirectory, settings.ContactSheetFileName);
            await CutsceneVideoOutputs.WriteContactSheetAsync(
                ffmpegPath,
                masterVideoPath,
                totalFrames,
                settings.ContactSheetColumns,
                settings.ContactSheetThumbnailWidth,
                settings.ContactSheetMaxThumbnails,
                contactSheetPath,
                cancellationToken).ConfigureAwait(false);
        }
        double contactSheetSeconds = Stopwatch.GetElapsedTime(contactSheetStartTimestamp).TotalSeconds;

        return new CutsceneRecordingResult(
            masterVideoPath,
            posterPath,
            optimizedPath,
            contactSheetPath,
            totalFrames,
            warmupSeconds,
            captureSeconds,
            optimizedSeconds,
            contactSheetSeconds);
    }

    /// <summary>
    /// Renders a frame shortly after the start of each camera cut clip until the scene is ready, so the programs and
    /// textures of every shot load before the capture. It then shows frame zero and waits again.
    /// </summary>
    private static async Task WarmUpAsync(CutsceneRecordingScene scene, int totalFrames, CancellationToken cancellationToken)
    {
        CutsceneRecordSettings settings = scene.Settings;
        List<double> cutStarts = scene.GetCameraCutStartSeconds();
        if (settings.CutWarmupRenderFrames > 0)
        {
            for (int i = 0; i < cutStarts.Count; i++)
            {
                // One frame after the cut start is inside the shot, also when the previous clip ends at the same time.
                int frame = Math.Clamp((int)Math.Ceiling(cutStarts[i] * settings.FramesPerSecond) + 1, 0, totalFrames - 1);
                await RunOnUpdateThreadAsync(() => scene.ApplyFrame(frame, forceJump: true), cancellationToken).ConfigureAwait(false);
                await WaitForReadyRenderFramesAsync(scene, settings.CutWarmupRenderFrames, cancellationToken).ConfigureAwait(false);
            }
        }

        await RunOnUpdateThreadAsync(() => scene.ApplyFrame(0, forceJump: true), cancellationToken).ConfigureAwait(false);
        await WaitForReadyRenderFramesAsync(scene, settings.WarmupRenderFrames, cancellationToken).ConfigureAwait(false);

        // The capture starts with a jump to frame zero, so the physics chains reset in the same way as in every recording.
        await RunOnUpdateThreadAsync(scene.ForgetLastFrame, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits until the scene has been ready for <paramref name="requiredReadyFrames"/> consecutive rendered frames and a
    /// render pipeline readback scope is free.
    /// </summary>
    private static async Task WaitForReadyRenderFramesAsync(
        CutsceneRecordingScene scene,
        int requiredReadyFrames,
        CancellationToken cancellationToken)
    {
        long waitStartTimestamp = Stopwatch.GetTimestamp();
        long? readyStartSequence = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool sceneReady = scene.IsReadyForCapture;
            bool readbackReady = false;
            if (sceneReady && scene.Viewport.TryEnterRenderPipelineReadbackScope(out IDisposable? probeScope))
            {
                probeScope?.Dispose();
                readbackReady = true;
            }

            long currentSequence = scene.Window.LastCompletedRenderWindowInterval.Sequence;
            if (sceneReady && readbackReady)
            {
                if (readyStartSequence is null)
                    readyStartSequence = currentSequence;
                else if (currentSequence >= readyStartSequence.Value + requiredReadyFrames)
                    return;
            }
            else
            {
                readyStartSequence = null;
            }

            TimeSpan elapsed = Stopwatch.GetElapsedTime(waitStartTimestamp);
            if (elapsed > ReadinessTimeout)
            {
                throw new TimeoutException(
                    $"Timed out after {elapsed.TotalSeconds:F1}s waiting for the recording viewport " +
                    $"(sceneReady={sceneReady}, readbackReady={readbackReady}, {scene.DescribeReadiness()}).");
            }

            await Task.Delay(16, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Records one capture segment: global frames <paramref name="segmentStart"/> up to
    /// <paramref name="segmentStart"/> + <paramref name="segmentFrames"/> - 1. The method starts one
    /// <see cref="ViewportSequenceCaptureSession"/> with the lockstep gate and <paramref name="frameSink"/> already set,
    /// waits until the session ends, and returns the final snapshot. It throws when a frame is missing.
    /// </summary>
    private static async Task<ViewportSequenceCaptureSnapshot> CaptureSegmentAsync(
        XRViewport viewport,
        CutsceneLockstepController lockstep,
        IViewportSequenceFrameSink frameSink,
        int segmentStart,
        int segmentFrames,
        string segmentRootDir,
        CancellationToken cancellationToken)
    {
        // The frame sink receives the pixels, so the session writes no PNG files, contact sheet, or difference data.
        if (!ViewportSequenceCaptureOptions.TryCreate(
                frameCount: segmentFrames,
                durationSeconds: null,
                frameStride: 1,
                captureFramesPerSecond: null,
                maxFrames: segmentFrames,
                outputScale: 1.0,
                maxInFlightReadbacks: 4,
                overflowPolicy: "drop",
                preserveAlpha: false,
                createContactSheet: false,
                contactSheetColumns: 1,
                contactSheetThumbnailWidth: 64,
                computeFrameDifferences: false,
                outputDirectory: segmentRootDir,
                out ViewportSequenceCaptureOptions? captureOptions,
                out string? optionsError) ||
            captureOptions is null)
        {
            throw new InvalidOperationException($"Invalid ViewportSequenceCaptureOptions: {optionsError}");
        }

        ViewportSequenceCaptureSession? session = null;
        long segmentStartTimestamp = Stopwatch.GetTimestamp();
        await RunOnUpdateThreadAsync(() =>
        {
            lockstep.Attach(segmentStart, segmentFrames);
            if (!ViewportSequenceCaptureManager.Instance.TryStart(
                    viewport,
                    captureOptions,
                    out session,
                    out string? startError,
                    configure: created =>
                    {
                        created.ShouldCaptureFrame = lockstep.ShouldCaptureFrame;
                        created.FrameScheduled = lockstep.OnFrameScheduled;
                        created.FrameSink = frameSink;
                    }) ||
                session is null)
            {
                lockstep.Detach();
                throw new InvalidOperationException($"Failed to start ViewportSequenceCaptureSession: {startError}");
            }
        }, cancellationToken).ConfigureAwait(false);

        Stopwatch captureWatch = Stopwatch.StartNew();
        long lastProgressTimestamp = Stopwatch.GetTimestamp();
        int lastSettledCount = -1;
        try
        {
            while (!session!.IsTerminal)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (int scheduledCount, int completedCount, int failedCount) = session.GetProgressCounts();
                int settledCount = completedCount + failedCount;
                if (settledCount != lastSettledCount)
                {
                    lastSettledCount = settledCount;
                    lastProgressTimestamp = Stopwatch.GetTimestamp();
                }

                TimeSpan sinceLastProgress = Stopwatch.GetElapsedTime(lastProgressTimestamp);
                if (captureWatch.Elapsed > SegmentTimeout || sinceLastProgress > SegmentNoProgressTimeout)
                {
                    _ = session.Cancel();
                    ViewportSequenceCaptureSnapshot timeoutSnapshot = session.CreateSnapshot(includeFrames: false);
                    throw new TimeoutException(
                        $"Timed out waiting for ViewportSequenceCapture segment at frame {segmentStart} " +
                        $"(scheduled={scheduledCount}, completed={completedCount}, failed={failedCount}, " +
                        $"dropped={timeoutSnapshot.DroppedFrameCount}, noProgressSeconds={sinceLastProgress.TotalSeconds:F1}).");
                }

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await RunOnUpdateThreadAsync(lockstep.Detach, CancellationToken.None).ConfigureAwait(false);
        }

        ViewportSequenceCaptureSnapshot snapshot = session.CreateSnapshot(includeFrames: true);
        double segmentSeconds = Stopwatch.GetElapsedTime(segmentStartTimestamp).TotalSeconds;
        Console.WriteLine(
            $"ViewportSequenceCapture segment finished: start={segmentStart}, state={snapshot.State}, stopReason={snapshot.StopReason}, " +
            $"scheduled={snapshot.ScheduledFrameCount}, completed={snapshot.CompletedFrameCount}, failed={snapshot.FailedFrameCount}, " +
            $"dropped={snapshot.DroppedFrameCount}, seconds={segmentSeconds:F1}, capturedFps={snapshot.CompletedFrameCount / Math.Max(segmentSeconds, 0.001):F2}.");

        if (!string.Equals(snapshot.State, "completed", StringComparison.OrdinalIgnoreCase) ||
            snapshot.CompletedFrameCount != segmentFrames)
        {
            throw new InvalidOperationException(
                $"ViewportSequenceCapture segment at frame {segmentStart} did not complete all {segmentFrames} frames " +
                $"(state={snapshot.State}, completed={snapshot.CompletedFrameCount}, error={snapshot.Error}).");
        }

        return snapshot;
    }

    /// <summary>Runs <paramref name="action"/> on the update thread and completes when it ends.</summary>
    public static Task RunOnUpdateThreadAsync(Action action, CancellationToken cancellationToken)
    {
        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Engine.EnqueueUpdateThreadTask(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                action();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task.WaitAsync(cancellationToken);
    }
}
