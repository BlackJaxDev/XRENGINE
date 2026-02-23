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
    // Per-render-pass drain: submits audio to OpenAL, uploads one video
    // frame to the GPU, and emits periodic A/V drift telemetry.
    //
    // All drain activity is single-threaded (render thread only). The
    // audio pump timer has been removed; the render thread at 60+ fps
    // provides sufficient pump frequency (~16 ms intervals).
    // ═══════════════════════════════════════════════════════════════════

    public partial class UIVideoComponent
    {
        // ═══════════════════════════════════════════════════════════════
        // Audio Drain
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Submits decoded audio frames to OpenAL and starts playback once
        /// enough audio is pre-buffered. Returns the audioSource and the
        /// current playable buffer count for the caller's video-gate logic.
        /// </summary>
        private void DrainStreamingAudio(IMediaStreamSession session,
                                         out AudioSourceComponent? audioSource,
                                         out int queuedAudioBuffers)
        {
            audioSource = _cachedAudioSource;

            // Late-init: the AudioSourceComponent may have been added to the sibling
            // list AFTER StartStreamingPipelineOnMainThread cached a null reference
            // (this happens when UIVideoComponent is added to a node before
            // AudioSourceComponent, e.g. UIEditorComponent adds them in that order).
            // Refresh the cache here so audio starts working without a restart.
            if (audioSource is null)
            {
                audioSource = GetSiblingComponent<AudioSourceComponent>();
                if (audioSource is not null)
                {
                    _cachedAudioSource = audioSource;
                    Debug.Out("[AV Setup] AudioSourceComponent discovered after pipeline start — cache refreshed. " +
                              "Consider adding AudioSourceComponent before UIVideoComponent to avoid this.");
                }
                else
                {
                    long nowNoSrc = GetEngineTimeTicks();
                    if (nowNoSrc - _lastNoListenersWarnTicks >= TimeSpan.TicksPerSecond * 5)
                    {
                        _lastNoListenersWarnTicks = nowNoSrc;
                        Debug.UIWarning("[AV Audio] No sibling AudioSourceComponent found — audio is disabled. " +
                                        "Add an AudioSourceComponent to the same scene node as UIVideoComponent.");
                    }
                }
            }

            SuppressAutoPlayOnAudioSources(audioSource);

            // Reclaim processed OpenAL buffers first so the pool has room for
            // new submissions and the BufferProcessed event fires, advancing
            // _processedSampleCount for the hardware clock.
            audioSource?.DequeueConsumedBuffers();

            // Transition any STOPPED source (from natural underrun or any other
            // cause) back to INITIAL before we start filling.  A stopped source
            // reports every newly-queued buffer as "processed" immediately, so
            // QueueBuffers churns through them one-at-a-time without accumulating
            // enough for Phase 2's pre-buffer gate.  Rewind() resets the source
            // to INITIAL state where BuffersProcessed = 0 and buffers accumulate
            // normally.  This is safe to call on an already-INITIAL source (no-op).
            //
            // Additionally, if the clock was previously seeded it means audio
            // was playing and hit a natural underrun.  During the gap, video
            // continued via wall-clock fallback and advanced past the audio
            // timeline.  Resetting clock accounting here lets SubmitDecodedAudioFrame
            // re-seed _firstAudioPts with its fast-forward logic, aligning the
            // audio clock to the current video position and eliminating drift.
            if (audioSource is not null)
            {
                bool anyRewound = false;
                foreach (var source in audioSource.ActiveListeners.Values)
                {
                    if (!source.IsPlaying && source.BuffersQueued == 0)
                    {
                        source.Rewind();
                        anyRewound = true;
                    }
                }

                if (anyRewound && _firstAudioPts != long.MinValue)
                {
                    Debug.Out($"[AV Audio] Underrun recovery: resetting clock accounting " +
                              $"(prevClock={GetAudioClock() / TimeSpan.TicksPerMillisecond}ms, " +
                              $"videoPts={_lastPresentedVideoPts / TimeSpan.TicksPerMillisecond}ms) " +
                              $"so clock re-seeds aligned to video position.");
                    _totalAudioDurationSubmittedTicks = 0;
                    _totalAudioBuffersSubmitted = 0;
                    _submittedSampleCounts.Clear();
                    _processedSampleCount = 0;
                    _firstAudioPts = long.MinValue;
                    _driftEwmaSeeded = false;
                    _driftEwmaTicks = 0;
                    _telemetryCatchupDrops = 0;
                }
            }

            queuedAudioBuffers = GetPlayableAudioBuffers(audioSource);

            // ── Phase 1: Fill the OpenAL queue ──
            int submittedThisCycle = 0;
            while (submittedThisCycle < MaxAudioFramesPerDrain)
            {
                // Stop filling once both depth targets are satisfied.
                if (GetEstimatedQueuedAudioDurationTicks(audioSource) >= TargetAudioQueuedDurationTicks &&
                    queuedAudioBuffers >= TargetOpenAlQueuedBuffers)
                    break;

                if (!session.TryDequeueAudioFrame(out DecodedAudioFrame audioFrame))
                    break;

                if (!SubmitDecodedAudioFrame(audioFrame, audioSource, out bool queueFull))
                {
                    if (queueFull)
                        break; // OpenAL pool at capacity — stop this cycle.
                    // else: no listeners or bad frame — skip and try next.
                }

                submittedThisCycle++;
                queuedAudioBuffers = GetPlayableAudioBuffers(audioSource);
            }

            bool hasListeners = audioSource is not null && !audioSource.ActiveListeners.IsEmpty;
            bool anyPlaying = false;
            if (hasListeners)
            {
                foreach (var source in audioSource!.ActiveListeners.Values)
                {
                    if (source.IsPlaying)
                    {
                        anyPlaying = true;
                        break;
                    }
                }
            }

            // NOTE: No forced Play() call here.  The Rewind() block above
            // ensures stopped sources transition to INITIAL state before
            // Phase 1 filling, so buffers accumulate correctly.  Phase 2
            // below handles the actual Play() after pre-buffering
            // MinAudioBuffersBeforePlay (8) buffers.

            // ── Phase 2: Start playback once pre-buffered ──
            if (GetEstimatedQueuedAudioDurationTicks(audioSource) >= MinAudioQueuedDurationBeforePlayTicks &&
                queuedAudioBuffers >= MinAudioBuffersBeforePlay &&
                audioSource is not null && !audioSource.ActiveListeners.IsEmpty)
            {
                foreach (var source in audioSource.ActiveListeners.Values)
                {
                    if (!source.IsPlaying)
                        source.Play();
                }
                _audioHasEverPlayed = true;
            }

            // ── Phase 3: Underrun telemetry ──
            if (_hasReceivedFirstFrame && audioSource is not null)
            {
                anyPlaying = false;
                foreach (var source in audioSource.ActiveListeners.Values)
                    if (source.IsPlaying) { anyPlaying = true; break; }

                if (_wasAudioPlaying && !anyPlaying)
                {
                    _telemetryAudioUnderruns++;
                    Debug.UIWarning($"[AV Audio] Underrun #{_telemetryAudioUnderruns}: " +
                                    $"queuedMs={GetEstimatedQueuedAudioDurationTicks(audioSource) / TimeSpan.TicksPerMillisecond} " +
                                    $"playableBuffers={queuedAudioBuffers}");
                }

                _wasAudioPlaying = anyPlaying;
            }

            queuedAudioBuffers = GetPlayableAudioBuffers(audioSource);
        }

        // ═══════════════════════════════════════════════════════════════
        // Main Drain Loop (render thread, called each render pass)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Full per-frame drain: audio fill → playback start → video present.
        /// <para>
        /// Video pacing uses the hardware audio clock as master:
        /// <list type="bullet">
        ///   <item>drift &gt; +<see cref="VideoHoldThresholdTicks"/> → hold (video ahead)</item>
        ///   <item>drift &lt; <see cref="VideoDropThresholdTicks"/> → drop (video behind)</item>
        ///   <item>drift &lt; <see cref="VideoResetThresholdTicks"/> → flush queues and resync</item>
        ///   <item>No audio clock yet → wall-clock fallback inside <see cref="HlsMediaStreamSession"/></item>
        /// </list>
        /// </para>
        /// </summary>
        private void DrainStreamingFramesOnMainThread(IMediaStreamSession session)
        {
            DrainStreamingAudio(session, out var audioSource, out int queuedAudioBuffers);

            long nowTicks = GetEngineTimeTicks();

            // ── Phase 4: Compute audio clock ──
            long audioClockTicks = GetAudioClock();
            bool audioSyncActive = audioClockTicks > 0;

            // Shift the clock forward by the estimated display pipeline latency
            // so video frames are presented ahead of the raw audio position.
            // By the time the GPU pipeline delivers them to the screen, the
            // audio will have caught up and A/V appear perceptually aligned.
            long videoClockTicks = audioSyncActive
                ? audioClockTicks + VideoDisplayLatencyCompensationTicks
                : audioClockTicks;

            // ── Phase 5: Video gate ──
            // When an audio source is present but hasn't started playing yet,
            // hold video until the clock is live.  Once audio has played at
            // least once in this session, allow wall-clock fallback immediately
            // so the user sees smooth video instead of a freeze during format-
            // change flushes and underrun recoveries.  On first startup (audio
            // never played yet), fall back only after 750 ms of stall with an
            // empty OpenAL queue (e.g. no AudioListenerComponent in the scene).
            bool hasAudioPipeline = audioSource is not null && !audioSource.ActiveListeners.IsEmpty;
            bool allowUnsyncedFallback = false;

            if (hasAudioPipeline && !audioSyncActive && _hasReceivedFirstFrame)
            {
                if (_audioHasEverPlayed)
                {
                    // Audio was active before — allow video to continue via
                    // wall-clock while audio re-buffers after format change
                    // or underrun recovery.
                    allowUnsyncedFallback = true;
                }
                else
                {
                    // First startup: conservative gate — wait for audio.
                    long sinceLastVideo = _lastVideoFrameTicks > 0 ? nowTicks - _lastVideoFrameTicks : 0;
                    bool noOpenAlAudio  = GetEstimatedQueuedAudioDurationTicks(audioSource) <= 0;

                    if (sinceLastVideo >= RebufferThresholdTicks && noOpenAlAudio)
                        allowUnsyncedFallback = true;
                }
            }

            if (allowUnsyncedFallback && !_audioSyncFallbackActive)
            {
                _audioSyncFallbackActive = true;
                Debug.UIWarning("[AV Sync] Audio stalled; falling back to unsynced video.");
            }
            else if (_audioSyncFallbackActive && (audioSyncActive || !hasAudioPipeline))
            {
                _audioSyncFallbackActive = false;
                Debug.Out("[AV Sync] Audio clock restored.");
            }

            bool videoGateOpen = !hasAudioPipeline || audioSyncActive || allowUnsyncedFallback;

            if (!videoGateOpen)
            {
                EmitDriftTelemetry(audioClockTicks);
                return;
            }

            // ── Phase 6: Dequeue video frames and present the latest due frame ──
            // When audio-clock sync is active, sweep through ALL frames whose
            // PTS is at-or-before the audio clock (within the presentable window)
            // and present only the most recent one.  Previous "presentable but
            // stale" frames are soft-dropped so video never gradually falls
            // behind audio due to render-rate < video-frame-rate.
            bool presented = false;

            const int maxDropsPerRender = 32;
            int drops = 0;

            bool hasFrameToPresent = false;
            DecodedVideoFrame frameToPresent = default;

            // ── Phase 6a: Compute adaptive drop threshold ──
            // When the per-frame drift EWMA indicates video is consistently
            // behind audio, tighten the drop window from the default −150 ms
            // so we catch up faster.  Without this, video can lag audio by
            // up to 149 ms indefinitely without ever triggering a drop.
            long effectiveDropThreshold = VideoDropThresholdTicks; // −150 ms
            if (_driftEwmaSeeded && _driftEwmaTicks < VideoCatchupDriftThresholdTicks)
            {
                // Target: half the EWMA magnitude behind the audio clock.
                effectiveDropThreshold = _driftEwmaTicks / 2;
                // Floor at ~1 frame at 60 fps (−16 ms) to avoid oscillation.
                const long minDropThreshold = -(TimeSpan.TicksPerMillisecond * 16);
                if (effectiveDropThreshold > minDropThreshold)
                    effectiveDropThreshold = minDropThreshold;
            }

            while (session.TryDequeueVideoFrame(videoClockTicks, out DecodedVideoFrame candidate))
            {
                long videoPts = candidate.PresentationTimestampTicks;

                if (audioSyncActive && videoPts > 0)
                {
                    long drift = videoPts - videoClockTicks;

                    // Frame is too far ahead of the audio clock — hold until
                    // the clock advances. The session also gates at this same
                    // threshold, so this check is a safety net for edge cases.
                    if (drift > VideoHoldThresholdTicks)
                        break;

                    // Extreme lag: flush queues and reset the clock so both
                    // streams can restart from a clean state.
                    if (drift < VideoResetThresholdTicks)
                    {
                        Debug.UIWarning($"[AV Sync] Extreme drift {drift / TimeSpan.TicksPerMillisecond}ms — " +
                                        $"flushing queues and reseeding clock.");
                        if (audioSource is not null)
                            FlushAudioQueue(audioSource);
                        // Clear submitted sample tracking so clock is reseeded on next audio frame.
                        _submittedSampleCounts.Clear();
                        _processedSampleCount = 0;
                        _firstAudioPts = long.MinValue;                        _driftEwmaSeeded = false;
                        _driftEwmaTicks = 0;
                        _telemetryCatchupDrops = 0;                        _telemetryVideoFramesDropped++;
                        drops++;
                        if (drops >= maxDropsPerRender) break;
                        continue;
                    }

                    // Normal drop: video lags too far, skip this frame.
                    if (drift < effectiveDropThreshold)
                    {
                        if (effectiveDropThreshold != VideoDropThresholdTicks)
                            _telemetryCatchupDrops++;
                        _telemetryVideoFramesDropped++;
                        drops++;
                        if (drops >= maxDropsPerRender) break;
                        continue;
                    }
                }

                // Frame is in the presentable window.  Mark it as the best
                // candidate so far — if a later frame is also due, it will
                // replace this one (the stale one counts as a soft-drop).
                if (hasFrameToPresent)
                    _telemetryVideoFramesDropped++;

                frameToPresent = candidate;
                hasFrameToPresent = true;

                // Without audio sync there is no reliable clock to sweep
                // against, so present the first available frame immediately.
                if (!audioSyncActive)
                    break;

                // With audio sync, keep sweeping: TryDequeueVideoFrame will
                // return false once the next queued frame is in the future
                // (PTS > videoClock + 40 ms), at which point we present the
                // latest candidate found so far. This ensures video always
                // shows the most current frame relative to the audio clock.
            }

            if (hasFrameToPresent)
            {
                ApplyDecodedVideoFrame(frameToPresent);
                _lastVideoFrameTicks = nowTicks;
                _inRebuffer = false;
                _hasReceivedFirstFrame = true;
                _telemetryVideoFramesPresented++;
                presented = true;
            }

            // ── Phase 7: Update drift EWMA ──
            // Track per-frame drift as an exponentially-weighted moving average
            // so the adaptive drop threshold can tighten when video is
            // consistently behind audio. Updated after presentation so the
            // metric reflects the frame we actually showed.
            if (hasFrameToPresent && audioSyncActive && frameToPresent.PresentationTimestampTicks > 0)
            {
                long frameDrift = frameToPresent.PresentationTimestampTicks - videoClockTicks;
                _lastPresentedDriftTicks = frameDrift;
                _driftIntervalMinTicks = _driftIntervalSamples == 0
                    ? frameDrift
                    : Math.Min(_driftIntervalMinTicks, frameDrift);
                _driftIntervalMaxTicks = _driftIntervalSamples == 0
                    ? frameDrift
                    : Math.Max(_driftIntervalMaxTicks, frameDrift);
                _driftIntervalSamples++;

                if (!_driftEwmaSeeded)
                {
                    _driftEwmaTicks = frameDrift;
                    _driftEwmaSeeded = true;
                }
                else
                {
                    _driftEwmaTicks = (long)(DriftEwmaAlpha * frameDrift + (1.0 - DriftEwmaAlpha) * _driftEwmaTicks);
                }
            }

            // ── Phase 7a: Brief drift monitor (every 2 s) ──
            // Log a compact drift status more frequently than the full telemetry
            // interval to make slow-growing drift visible in logs.
            if (_driftEwmaSeeded && audioSyncActive)
            {
                long driftNow = GetEngineTimeTicks();
                if (driftNow - _driftStatusLastLogTicks >= DriftStatusIntervalTicks)
                {
                    _driftStatusLastLogTicks = driftNow;
                    long ewmaMs = _driftEwmaTicks / TimeSpan.TicksPerMillisecond;
                    long debtMs = _lastPresentedVideoPts > 0
                        ? (videoClockTicks - _lastPresentedVideoPts) / TimeSpan.TicksPerMillisecond
                        : 0;
                    long threshMs = effectiveDropThreshold / TimeSpan.TicksPerMillisecond;
                    if (Math.Abs(ewmaMs) > 5 || Math.Abs(debtMs) > 5 || _telemetryCatchupDrops > 0)
                    {
                        Debug.Out($"[AV Drift] ewma={ewmaMs}ms debt={debtMs}ms " +
                                  $"catchupDrops={_telemetryCatchupDrops} threshold={threshMs}ms");
                    }
                }
            }

            if (!presented && session.IsOpen && _lastVideoFrameTicks > 0)
            {
                // ── Phase 8: Rebuffer detection ──
                long gap = nowTicks - _lastVideoFrameTicks;
                if (!_inRebuffer && gap >= RebufferThresholdTicks)
                {
                    _inRebuffer = true;
                    _rebufferCount++;
                    Debug.UIWarning($"[AV Video] Rebuffer #{_rebufferCount}: gapMs={gap / TimeSpan.TicksPerMillisecond}");
                }
            }

            EmitDriftTelemetry(audioClockTicks);
        }

        // ═══════════════════════════════════════════════════════════════
        // Video Frame — GPU Upload
        // ═══════════════════════════════════════════════════════════════

        private void ApplyDecodedVideoFrame(DecodedVideoFrame frame)
        {
            if (frame.Width > 0 && frame.Height > 0)
                WidthHeight = new IVector2(frame.Width, frame.Height);

            _lastPresentedVideoPts = frame.PresentationTimestampTicks;

            if (frame.PixelFormat != VideoPixelFormat.Rgb24 || frame.PackedData.IsEmpty)
                return;

            string? uploadError = null;
            if (_gpuVideoActions?.UploadVideoFrame(frame, VideoTexture, out uploadError) == true)
                return;

            if (!string.IsNullOrWhiteSpace(uploadError))
                Debug.UIWarning(uploadError);
        }

        // ═══════════════════════════════════════════════════════════════
        // Telemetry
        // ═══════════════════════════════════════════════════════════════

        private void EmitDriftTelemetry(long audioClockTicks)
        {
            long now = GetEngineTimeTicks();
            if (_telemetryLastLogTicks > 0 && now - _telemetryLastLogTicks < TelemetryIntervalTicks)
                return;

            _telemetryLastLogTicks = now;

            if (!_hasReceivedFirstFrame)
                return;

            // Raw drift: videoPts vs raw audio clock (no display compensation).
            long rawDriftTicks = 0;
            // Effective drift: videoPts vs display-compensated clock (what pacing actually uses).
            long effectiveDriftTicks = 0;
            if (audioClockTicks > 0 && _lastPresentedVideoPts > 0)
            {
                rawDriftTicks = _lastPresentedVideoPts - audioClockTicks;
                long videoClk = audioClockTicks + VideoDisplayLatencyCompensationTicks;
                effectiveDriftTicks = _lastPresentedVideoPts - videoClk;
            }

            double audioClockMs = audioClockTicks / (double)TimeSpan.TicksPerMillisecond;
            double videoPtsMs = _lastPresentedVideoPts > 0
                ? _lastPresentedVideoPts / (double)TimeSpan.TicksPerMillisecond
                : 0.0;
            double rawDriftMs = rawDriftTicks / (double)TimeSpan.TicksPerMillisecond;
            double effectiveDriftMs = effectiveDriftTicks / (double)TimeSpan.TicksPerMillisecond;
            double ewmaMs = _driftEwmaTicks / (double)TimeSpan.TicksPerMillisecond;
            double presentDriftMs = _lastPresentedDriftTicks / (double)TimeSpan.TicksPerMillisecond;
            double frameAgeMs = _lastVideoFrameTicks > 0
                ? (now - _lastVideoFrameTicks) / (double)TimeSpan.TicksPerMillisecond
                : 0.0;
            long queuedMs = GetEstimatedQueuedAudioDurationTicks(_cachedAudioSource) / TimeSpan.TicksPerMillisecond;

            string driftRange = _driftIntervalSamples > 0
                ? $"{_driftIntervalMinTicks / (double)TimeSpan.TicksPerMillisecond:F2}..{_driftIntervalMaxTicks / (double)TimeSpan.TicksPerMillisecond:F2}"
                : "n/a";

            Debug.Out($"[AV Telemetry] drift={effectiveDriftMs:F2}ms rawDrift={rawDriftMs:F2}ms " +
                      $"presentDrift={presentDriftMs:F2}ms presentRange={driftRange}ms samples={_driftIntervalSamples} " +
                      $"ewma={ewmaMs:F2}ms frameAge={frameAgeMs:F2}ms " +
                      $"clock={audioClockMs:F2}ms vPts={videoPtsMs:F2}ms " +
                      $"vPresented={_telemetryVideoFramesPresented} vDropped={_telemetryVideoFramesDropped} " +
                      $"catchupDrops={_telemetryCatchupDrops} " +
                      $"aSubmitted={_telemetryAudioFramesSubmitted} aUnderruns={_telemetryAudioUnderruns} " +
                      $"rebuffers={_rebufferCount} audioQueuedMs={queuedMs} " +
                      $"processedSamples={_processedSampleCount} sampleOffset={_primaryAudioSource?.SampleOffset ?? 0}");

            // Reset present-time drift window for the next telemetry interval.
            _driftIntervalSamples = 0;
            _driftIntervalMinTicks = long.MaxValue;
            _driftIntervalMaxTicks = long.MinValue;
        }
    }
}
