using System;
using System.Threading;
using XREngine.Components;
using XREngine.Data.Vectors;
using XREngine.Rendering.VideoStreaming;
using XREngine.Rendering.VideoStreaming.Interfaces;

namespace XREngine.Rendering.UI
{
    // ═══════════════════════════════════════════════════════════════════
    // UIVideoComponent — Frame Drain Loop & Video Upload
    //
    // Per-render-pass drain that dequeues decoded audio/video frames,
    // submits audio to OpenAL, uploads one video frame to the GPU via
    // the active renderer's upload path (PBO for OpenGL, staging
    // buffer for Vulkan), detects rebuffer stalls, and emits A/V
    // drift telemetry.
    // ═══════════════════════════════════════════════════════════════════

    public partial class UIVideoComponent
    {
        /// <summary>
        /// Callback for the dedicated <see cref="_audioPumpTimer"/>. Runs on a
        /// thread-pool thread every ~10 ms, completely independent of the
        /// engine update/render loop frequency. This guarantees audio
        /// submission continues even during GC pauses, render stalls, or when
        /// the window is unfocused (engine target FPS = 0).
        /// </summary>
        private void AudioPumpTimerCallback(object? state)
        {
            if (_streamingSession is null)
                return;

            DrainStreamingAudio(_streamingSession, out _, out _);
        }

        /// <summary>
        /// Drains and submits audio only. Safe to call from both update and
        /// render paths; reentrancy is prevented by <see cref="_audioDrainInProgress"/>.
        /// </summary>
        private void DrainStreamingAudio(IMediaStreamSession session, out AudioSourceComponent? audioSource, out int queuedAudioBuffers)
        {
            audioSource = AudioSource;
            queuedAudioBuffers = GetPlayableAudioBuffers(audioSource);

            if (Interlocked.CompareExchange(ref _audioDrainInProgress, 1, 0) != 0)
                return;

            try
            {
                SuppressAutoPlayOnAudioSources(audioSource);
                UpdateAudioClock(audioSource);

                // ── Phase 1: Audio — fill OpenAL queue ──
                // Reclaim consumed buffers FIRST so the playable count is accurate
                // and OpenAL has maximum capacity for new submissions. Without this,
                // processed (already-played) buffers inflate BuffersQueued, causing
                // QueueBuffers to silently drop incoming audio when the pool ceiling
                // (MaxStreamingBuffers) is reached.
                audioSource?.DequeueConsumedBuffers();

                int submittedAudioFrames = 0;
                while (submittedAudioFrames < MaxAudioFramesPerDrain)
                {
                    // Stop once both buffering targets are met.
                    if (GetEstimatedQueuedAudioDurationTicks(audioSource) >= TargetAudioQueuedDurationTicks &&
                        queuedAudioBuffers >= TargetOpenAlQueuedBuffers)
                        break;

                    if (!session.TryDequeueAudioFrame(out DecodedAudioFrame audioFrame))
                        break;

                    if (!SubmitDecodedAudioFrame(audioFrame, audioSource))
                        break; // OpenAL queue full or bad frame — stop draining to avoid losing already-dequeued frames.

                    submittedAudioFrames++;
                    queuedAudioBuffers = GetPlayableAudioBuffers(audioSource);
                }

                // ── Phase 2: Audio — start playback once pre-buffered ──
                // Use lower thresholds during underrun recovery so audio resumes
                // within ~200 ms instead of imposing a 1.5 s silence gap.
                long minDuration = _audioUnderrunRecovery ? UnderrunRecoveryMinDurationTicks : MinAudioQueuedDurationBeforePlayTicks;
                int  minBuffers  = _audioUnderrunRecovery ? UnderrunRecoveryMinBuffers : MinAudioBuffersBeforePlay;

                if (GetEstimatedQueuedAudioDurationTicks(audioSource) >= minDuration &&
                    queuedAudioBuffers >= minBuffers && audioSource is not null &&
                    !audioSource.ActiveListeners.IsEmpty)
                {
                    // Check the *real* OpenAL source state, not the component-level
                    // State property. AudioSourceComponent.Play() uses SetField,
                    // which is a no-op when State is already Playing — so a source
                    // that stopped after buffer exhaustion would never be restarted.
                    // Calling AudioSource.Play() directly via the OpenAL wrapper
                    // always issues alSourcePlay, restarting the source.
                    foreach (var source in audioSource.ActiveListeners.Values)
                    {
                        if (!source.IsPlaying)
                            source.Play();
                    }

                    // Clear recovery mode once playback is re-established.
                    _audioUnderrunRecovery = false;
                }

                // ── Phase 3: Audio — track underrun transitions for telemetry ──
                // An underrun is when an OpenAL source that was previously playing
                // transitions to Stopped because it ran out of buffers. This is the
                // actual audible glitch. Checking the source state is more reliable
                // than the buffer count, which can transiently read as zero during
                // normal queue housekeeping.
                if (_hasReceivedFirstFrame && audioSource is not null)
                {
                    bool anySourcePlaying = audioSource.ActiveListeners.Values.Any(static s => s.IsPlaying);

                    if (_wasAudioPlaying && !anySourcePlaying)
                    {
                        // Source was playing last frame but stopped now → underrun.
                        _telemetryAudioUnderruns++;
                        _audioUnderrunRecovery = true;
                        long queuedMsAtUnderrun = GetEstimatedQueuedAudioDurationTicks(audioSource) / TimeSpan.TicksPerMillisecond;
                        int playableBuffersAtUnderrun = GetPlayableAudioBuffers(audioSource);
                        int sourceCount = audioSource.ActiveListeners.Count;
                        Debug.UIWarning($"[AV Audio] Underrun detected: count={_telemetryAudioUnderruns}, queuedMs={queuedMsAtUnderrun}, playableBuffers={playableBuffersAtUnderrun}, activeSources={sourceCount}, submittedThisDrain={submittedAudioFrames}, targetQueuedMs={TargetAudioQueuedDurationTicks / TimeSpan.TicksPerMillisecond}, minBeforePlayMs={MinAudioQueuedDurationBeforePlayTicks / TimeSpan.TicksPerMillisecond}");
                    }

                    _wasAudioPlaying = anySourcePlaying;
                }

                // ── Phase 4: Adaptive catch-up pitch ──
                UpdateCatchUpPitch(audioSource);

                // Return latest queue depth after processing.
                queuedAudioBuffers = GetPlayableAudioBuffers(audioSource);
            }
            finally
            {
                Interlocked.Exchange(ref _audioDrainInProgress, 0);
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Frame Drain Loop (runs on render thread each render pass)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Main per-frame drain loop. Called synchronously on the render thread
        /// during uniform setup. Performs in order:
        /// <list type="number">
        ///   <item>Suppress auto-play on audio sources (we control playback start).</item>
        ///   <item>Advance the software audio clock.</item>
        ///   <item>Submit decoded audio frames to OpenAL (before dequeuing consumed
        ///         buffers to keep the queue maximally full).</item>
        ///   <item>Reclaim processed OpenAL buffers.</item>
        ///   <item>Start OpenAL playback once enough audio is pre-buffered.</item>
        ///   <item>Track audio underrun transitions for telemetry.</item>
        ///   <item>Update adaptive catch-up pitch.</item>
        ///   <item>Dequeue and upload one video frame (dropping stale frames).</item>
        ///   <item>Detect rebuffer stalls and emit periodic A/V drift telemetry.</item>
        /// </list>
        /// </summary>
        private void DrainStreamingFramesOnMainThread(IMediaStreamSession session)
        {
            DrainStreamingAudio(session, out var audioSource, out int queuedAudioBuffers);

            // ── Phase 5: Video — dequeue and present one frame ──
            bool audioSyncActive = queuedAudioBuffers > 0 &&
                                   audioSource?.ActiveListeners.Values.Any(static source => source.IsPlaying) == true;
            long audioClockTicks = audioSyncActive ? GetAudioClockForVideoSync(audioSource) : 0;
            bool hasVideoFrame = false;
            DecodedVideoFrame videoFrame = default;

            // Startup gate: don't present the first video frame until audio is
            // actually playing. This prevents startup desync where video races
            // ahead during audio pre-buffer. After first presentation, keep
            // draining video even if audio briefly underruns to avoid visible
            // freezes from repeatedly re-applying the gate.
            bool hasAudioPipeline = audioSource is not null && !audioSource.ActiveListeners.IsEmpty;
            bool startupAudioGateOpen = !hasAudioPipeline || audioSyncActive || _hasReceivedFirstFrame;

            // Dequeue at most one video frame per render pass. The session's
            // TryDequeueVideoFrame releases a frame only when its PTS is at or
            // behind the audio clock, so video stays locked to audio. One frame
            // per call keeps catch-up smooth during stalls (the render loop at
            // 60 fps naturally consumes past-due frames one per pass rather
            // than in a visible burst).
            if (startupAudioGateOpen && session.TryDequeueVideoFrame(audioClockTicks, out DecodedVideoFrame candidate))
            {
                videoFrame = candidate;
                hasVideoFrame = true;
            }

            if (hasVideoFrame)
            {
                ApplyDecodedVideoFrame(videoFrame);
                _lastVideoFrameTicks = GetEngineTimeTicks();
                _inRebuffer = false;
                _hasReceivedFirstFrame = true;
                _telemetryVideoFramesPresented++;
            }
            else if (session.IsOpen && _lastVideoFrameTicks > 0)
            {
                // ── Phase 6: Rebuffer detection ──
                long gapTicks = GetEngineTimeTicks() - _lastVideoFrameTicks;
                if (!_inRebuffer && gapTicks >= RebufferThresholdTicks)
                {
                    _inRebuffer = true;
                    _rebufferCount++;
                    long gapMs = gapTicks / TimeSpan.TicksPerMillisecond;
                    Debug.UIWarning($"Streaming rebuffer detected: count={_rebufferCount}, gapMs={gapMs}");
                }
            }

            // ── Phase 7: Telemetry ──
            EmitDriftTelemetry();
        }

        /// <summary>
        /// Determines whether a video frame should be dropped because it
        /// lags too far behind the audio clock.
        /// </summary>
        private static bool ShouldDropVideoFrame(long videoPtsTicks, long audioClockTicks)
        {
            return audioClockTicks > 0 && videoPtsTicks + MaxVideoLagBehindAudioTicks + TargetVideoBufferTicks < audioClockTicks;
        }

        // ═══════════════════════════════════════════════════════════════
        // Video Frame — GPU Upload
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Applies a decoded video frame by resizing the texture (if needed)
        /// and uploading the pixel data via the active renderer's GPU upload path.
        /// </summary>
        private void ApplyDecodedVideoFrame(DecodedVideoFrame frame)
        {
            // Resize the texture to match the stream resolution.
            if (frame.Width > 0 && frame.Height > 0)
                WidthHeight = new IVector2(frame.Width, frame.Height);

            _lastPresentedVideoPts = frame.PresentationTimestampTicks;

            // Only RGB24 packed data is supported for GPU upload.
            if (frame.PixelFormat != VideoPixelFormat.Rgb24 || frame.PackedData.IsEmpty)
                return;

            // Upload via the renderer's GPU upload path for efficient transfer.
            string? uploadError = null;
            if (_gpuVideoActions?.UploadVideoFrame(frame, VideoTexture, out uploadError) == true)
                return;

            if (!string.IsNullOrWhiteSpace(uploadError))
                Debug.UIWarning(uploadError);
        }

        // ═══════════════════════════════════════════════════════════════
        // Telemetry
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Logs periodic A/V drift telemetry (every <see cref="TelemetryIntervalTicks"/>).
        /// Reports video/audio PTS drift, frame counts, drop/underrun rates,
        /// rebuffer events, estimated audio queue depth, latency, and pitch.
        /// </summary>
        private void EmitDriftTelemetry()
        {
            long now = GetEngineTimeTicks();
            if (_telemetryLastLogTicks > 0 && now - _telemetryLastLogTicks < TelemetryIntervalTicks)
                return;

            _telemetryLastLogTicks = now;

            if (!_hasReceivedFirstFrame)
                return;

            long driftTicks = 0;
            long audioClockForTelemetry = GetAudioClockForVideoSync(AudioSource);
            if (audioClockForTelemetry > 0 && _lastPresentedVideoPts > 0)
                driftTicks = _lastPresentedVideoPts - audioClockForTelemetry;

            long driftMs = driftTicks / TimeSpan.TicksPerMillisecond;
            long estimatedQueuedMs = GetEstimatedQueuedAudioDurationTicks(AudioSource) / TimeSpan.TicksPerMillisecond;
            Debug.Out($"[AV Telemetry] drift={driftMs}ms, vPresented={_telemetryVideoFramesPresented}, vDropped={_telemetryVideoFramesDropped}, aSubmitted={_telemetryAudioFramesSubmitted}, aUnderruns={_telemetryAudioUnderruns}, rebuffers={_rebufferCount}, audioQueuedMs={estimatedQueuedMs}, latencyMs={_playbackLatencyMs:F0}, pitch={_currentPitch:F3}");
        }
    }
}
