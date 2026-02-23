using System;
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
            var audioSource = AudioSource;
            SuppressAutoPlayOnAudioSources(audioSource);
            UpdateAudioClock(audioSource);

            // ── Phase 1: Audio — fill OpenAL queue (submit BEFORE dequeue) ──
            // Submit new audio BEFORE dequeuing consumed buffers so the OpenAL
            // queue stays as full as possible during the frame. This prevents a
            // brief low-water-mark window that can cause the source to starve.
            int queuedAudioBuffers = GetPlayableAudioBuffers(audioSource);
            int submittedAudioFrames = 0;
            while ((GetEstimatedQueuedAudioDurationTicks(audioSource) < TargetAudioQueuedDurationTicks || queuedAudioBuffers < TargetOpenAlQueuedBuffers) &&
                   submittedAudioFrames < MaxAudioFramesPerDrain &&
                   session.TryDequeueAudioFrame(out DecodedAudioFrame audioFrame))
            {
                if (!SubmitDecodedAudioFrame(audioFrame, audioSource))
                    continue;

                submittedAudioFrames++;
                queuedAudioBuffers = GetPlayableAudioBuffers(audioSource);
            }

            // Reclaim processed buffer objects AFTER submission so the OpenAL
            // source never transiently runs dry between dequeue and refill.
            audioSource?.DequeueConsumedBuffers();

            // ── Phase 2: Audio — start playback once pre-buffered ──
            if (GetEstimatedQueuedAudioDurationTicks(audioSource) >= MinAudioQueuedDurationBeforePlayTicks &&
                queuedAudioBuffers >= MinAudioBuffersBeforePlay && audioSource is not null &&
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
                }

                _wasAudioPlaying = anySourcePlaying;
            }

            // ── Phase 4: Adaptive catch-up pitch ──
            UpdateCatchUpPitch(audioSource);

            // ── Phase 5: Video — dequeue and present one frame ──
            bool audioSyncActive = queuedAudioBuffers > 0 &&
                                   audioSource?.ActiveListeners.Values.Any(static source => source.IsPlaying) == true;
            long audioClockTicks = audioSyncActive ? GetAudioClockForVideoSync() : 0;
            bool hasVideoFrame = false;
            DecodedVideoFrame videoFrame = default;

            // Present at most one frame per render pass to avoid visible
            // skipping caused by draining multiple due frames and only showing
            // the most recent one.
            while (session.TryDequeueVideoFrame(audioClockTicks, out DecodedVideoFrame candidate))
            {
                if (ShouldDropVideoFrame(candidate.PresentationTimestampTicks, audioClockTicks))
                {
                    _telemetryVideoFramesDropped++;
                    continue;
                }

                videoFrame = candidate;
                hasVideoFrame = true;
                break;
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
            if (_audioClockTicks > 0 && _lastPresentedVideoPts > 0)
                driftTicks = _lastPresentedVideoPts - _audioClockTicks;

            long driftMs = driftTicks / TimeSpan.TicksPerMillisecond;
            long estimatedQueuedMs = GetEstimatedQueuedAudioDurationTicks(AudioSource) / TimeSpan.TicksPerMillisecond;
            Debug.Out($"[AV Telemetry] drift={driftMs}ms, vPresented={_telemetryVideoFramesPresented}, vDropped={_telemetryVideoFramesDropped}, aSubmitted={_telemetryAudioFramesSubmitted}, aUnderruns={_telemetryAudioUnderruns}, rebuffers={_rebufferCount}, audioQueuedMs={estimatedQueuedMs}, latencyMs={_playbackLatencyMs:F0}, pitch={_currentPitch:F3}");
        }
    }
}
