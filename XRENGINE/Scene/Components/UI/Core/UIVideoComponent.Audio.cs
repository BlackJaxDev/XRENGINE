using System;
using XREngine.Audio;
using XREngine.Components;
using XREngine.Rendering.VideoStreaming;

namespace XREngine.Rendering.UI
{
    // ═══════════════════════════════════════════════════════════════════
    // UIVideoComponent — Audio Submission & Hardware Clock
    //
    // Handles OpenAL audio buffer submission (with PCM format conversion),
    // the hardware sample-position master clock for A/V sync, and
    // queue-depth estimation used by the drain-stop condition.
    //
    // Master clock formula:
    //   audioPts = _firstAudioPts
    //            + (_processedSampleCount + source.SampleOffset)
    //              * TimeSpan.TicksPerSecond / sampleRate
    //            - HardwareAudioLatencyTicks
    //
    // _processedSampleCount is advanced by OnPrimaryBufferProcessed which
    // fires synchronously inside DequeueConsumedBuffers on the render thread,
    // one tick per unqueued OpenAL buffer. Thread safety is guaranteed because
    // all drain activity is now single-threaded (render thread only).
    // ═══════════════════════════════════════════════════════════════════

    public partial class UIVideoComponent
    {
        // ═══════════════════════════════════════════════════════════════
        // Diagnostic Throttle Fields
        // ═══════════════════════════════════════════════════════════════

        /// <summary>Throttle: last engine-tick at which a "no listeners" warning was emitted.</summary>
        private long _lastNoListenersWarnTicks;

        /// <summary>Throttle: last engine-tick at which a listener-count diagnostic was emitted.</summary>
        private long _lastListenerDiagnosticTicks;

        /// <summary>Set after the one-shot startup diagnostic is emitted on the first audio frame submission attempt.</summary>
        private bool _audioStartupDiagLogged;

        // ═══════════════════════════════════════════════════════════════
        // Audio Frame — OpenAL Submission
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Converts a decoded audio frame to PCM16, handles format-change detection
        /// (which requires flushing the OpenAL queue), enqueues the buffer, and
        /// records the sample count so the hardware clock can track it.
        /// </summary>
        /// <param name="queueFull">
        /// Set to <c>true</c> when OpenAL's buffer pool is at capacity (normal
        /// backpressure — caller should stop draining this cycle).
        /// <c>false</c> on any other failure (no listeners, bad frame — caller may continue).
        /// </param>
        /// <returns><c>true</c> if the frame was successfully queued.</returns>
        private bool SubmitDecodedAudioFrame(DecodedAudioFrame frame, AudioSourceComponent? audioSource, out bool queueFull)
        {
            queueFull = false;

            if (audioSource is null || frame.InterleavedData.IsEmpty ||
                !TryConvertToOpenAlPcm16(frame, out short[] pcm16, out bool stereo) || pcm16.Length == 0)
            {
                // One-shot diagnostic when audioSource is null (common ordering race).
                if (!_audioStartupDiagLogged && audioSource is null)
                {
                    _audioStartupDiagLogged = true;
                    Debug.UIWarning("[AV Audio Diag] First audio frame dropped: audioSource is null. " +
                                    "AudioSourceComponent was not a sibling when the pipeline started. " +
                                    "The drain loop will auto-fix this on the next cycle.");
                }
                return false;
            }

            // One-shot startup diagnostic — capture the full audio pipeline state on the
            // first frame that actually reaches the OpenAL submission stage.
            if (!_audioStartupDiagLogged)
            {
                _audioStartupDiagLogged = true;
                Debug.Out($"[AV Audio Diag] First frame reached submission: " +
                          $"rate={frame.SampleRate} ch={frame.ChannelCount} fmt={frame.SampleFormat} " +
                          $"pts={frame.PresentationTimestampTicks} bytes={frame.InterleavedData.Length} " +
                          $"pcm16={pcm16.Length} stereo={stereo} | " +
                          $"ExternalBufMgmt={audioSource.ExternalBufferManagement} " +
                          $"MaxBufs={audioSource.MaxStreamingBuffers} " +
                          $"listeners={audioSource.ActiveListeners.Count} " +
                          $"primarySrc={(_primaryAudioSource != null ? "set" : "null")} " +
                          $"firstPts={(_firstAudioPts == long.MinValue ? "unseeded" : _firstAudioPts.ToString())}");
            }

            // If no listener has registered yet there is nowhere to submit audio.
            // Log a throttled setup warning and return without stopping the drain
            // loop so we keep trying on subsequent frames.
            if (audioSource.ActiveListeners.IsEmpty)
            {
                long nowTicks = GetEngineTimeTicks();
                if (nowTicks - _lastNoListenersWarnTicks >= TimeSpan.TicksPerSecond)
                {
                    _lastNoListenersWarnTicks = nowTicks;
                    Debug.UIWarning("[AV Audio] No active listeners on AudioSourceComponent — audio cannot be " +
                                    "submitted to OpenAL. Ensure an AudioListenerComponent exists in the scene " +
                                    "and is within audible range of this entity.");
                }
                return false;
            }

            int sampleRate = Math.Clamp(frame.SampleRate, 8000, 192000);

            // OpenAL requires all queued buffers on a source to share the same
            // format. HLS segment transitions can change sample rate or channel
            // count; detect the change and flush before submitting.
            if (_lastSubmittedAudioSampleRate != 0 &&
                (_lastSubmittedAudioSampleRate != sampleRate || _lastSubmittedAudioStereo != stereo))
            {
                Debug.UIWarning($"[AV Audio] Format change: oldRate={_lastSubmittedAudioSampleRate} newRate={sampleRate} " +
                                $"oldStereo={_lastSubmittedAudioStereo} newStereo={stereo}. Flushing OpenAL queue.");
                FlushAudioQueue(audioSource);
            }
            _lastSubmittedAudioSampleRate = sampleRate;
            _lastSubmittedAudioStereo = stereo;

            if (!audioSource.EnqueueStreamingBuffers(sampleRate, stereo, pcm16))
            {
                queueFull = true;
                return false;
            }

            // Seed the hardware clock from the first successfully submitted
            // frame. Some streams begin at PTS=0, so treat zero as valid.
            // Clamp negative start PTS to 0 to keep the startup gate stable.
            if (_firstAudioPts == long.MinValue)
            {
                long audioPts = Math.Max(0, frame.PresentationTimestampTicks);

                // If video has already been playing via wall-clock fallback and
                // is significantly ahead of where audio is starting, fast-forward
                // the clock baseline so video doesn't freeze waiting for the
                // audio clock to catch up from zero.  This makes the audio clock
                // start near the current video PTS, keeping playback smooth
                // across the wall-clock → audio-clock transition.  The drop/hold
                // thresholds handle any residual alignment from this point on.
                if (_lastPresentedVideoPts > audioPts + VideoHoldThresholdTicks)
                {
                    long headStartMs = (_lastPresentedVideoPts - audioPts) / TimeSpan.TicksPerMillisecond;
                    audioPts = _lastPresentedVideoPts;
                    Debug.Out($"[AV Audio] Clock fast-forwarded by {headStartMs}ms to match video position.");
                }

                _firstAudioPts = audioPts;
                Debug.Out($"[AV Audio] Clock seeded from first submitted frame: pts={_firstAudioPts}.");
            }

            // Record per-buffer sample count for the hardware clock.
            // pcm16 is always output as stereo (2 channels), so sample-frames = pcm16.Length / 2.
            int sampleFrames = pcm16.Length / 2;
            _submittedSampleCounts.Enqueue(sampleFrames);
            _primaryAudioSampleRate = sampleRate;

            // Track totals for queue-depth estimation (drain-stop condition).
            long frameDurationTicks = EstimateAudioDurationTicks(sampleRate, 2, sizeof(short), pcm16.Length * sizeof(short));
            _totalAudioDurationSubmittedTicks += frameDurationTicks;
            _totalAudioBuffersSubmitted++;
            _telemetryAudioFramesSubmitted++;
            return true;
        }

        // ═══════════════════════════════════════════════════════════════
        // Hardware Clock — Primary Source Management
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Ensures the primary OpenAL source (clock reference) is the first
        /// active listener source. Re-subscribes to <see cref="AudioSource.BufferProcessed"/>
        /// whenever the source changes. Called each drain cycle from
        /// <see cref="SuppressAutoPlayOnAudioSources"/>.
        /// </summary>
        private void UpdatePrimaryAudioSource(AudioSourceComponent? audioSource)
        {
            AudioSource? candidate = null;
            if (audioSource is not null)
            {
                foreach (var src in audioSource.ActiveListeners.Values)
                {
                    candidate = src;
                    break;
                }
            }

            if (candidate == _primaryAudioSource)
                return;

            string prevDesc = _primaryAudioSource is null ? "null" : "set";
            string nextDesc = candidate is null ? "null" : "set";
            Debug.Out($"[AV Audio] Primary source changed: {prevDesc} → {nextDesc}. " +
                      $"processedSamples={_processedSampleCount} " +
                      $"firstPts={(_firstAudioPts == long.MinValue ? "unseeded" : _firstAudioPts.ToString())}");

            if (_primaryAudioSource is not null)
                _primaryAudioSource.BufferProcessed -= OnPrimaryBufferProcessed;

            _primaryAudioSource = candidate;

            if (_primaryAudioSource is not null)
                _primaryAudioSource.BufferProcessed += OnPrimaryBufferProcessed;
        }

        /// <summary>
        /// Fired synchronously by the primary <see cref="AudioSource"/> whenever
        /// <see cref="AudioSource.UnqueueConsumedBuffers"/> finishes one buffer.
        /// Pops the matching sample-frame count and adds it to the running total
        /// so the hardware clock reflects exactly what has been played out.
        /// </summary>
        private void OnPrimaryBufferProcessed(AudioBuffer _)
        {
            if (_submittedSampleCounts.TryDequeue(out int sampleFrames))
                _processedSampleCount += sampleFrames;
        }

        // ═══════════════════════════════════════════════════════════════
        // Hardware Clock — Query
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Returns the current audio playback position as a stream PTS in .NET
        /// ticks (100 ns units), derived directly from the OpenAL hardware sample
        /// counter. Returns <c>0</c> when the clock is not yet seeded or no audio
        /// is playing.
        /// <para>
        /// Formula:
        /// <c>audioPts = _firstAudioPts
        ///             + (_processedSampleCount + source.SampleOffset)
        ///               * TicksPerSecond / sampleRate
        ///             - HardwareAudioLatencyTicks</c>
        /// </para>
        /// </summary>
        private long GetAudioClock()
        {
            if (_primaryAudioSource is null || _primaryAudioSampleRate <= 0 ||
                _firstAudioPts == long.MinValue || !_primaryAudioSource.IsPlaying)
                return 0;

            long totalSamples = _processedSampleCount + _primaryAudioSource.SampleOffset;
            long elapsed = totalSamples * TimeSpan.TicksPerSecond / _primaryAudioSampleRate;
            long clock = _firstAudioPts + elapsed - HardwareAudioLatencyTicks;
            return clock > 0 ? clock : 0;
        }

        // ═══════════════════════════════════════════════════════════════
        // Audio Queue Helpers
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Stops all active OpenAL sources, flushes their buffers, and resets
        /// the clock accounting state. Called on audio format changes.
        /// </summary>
        private void FlushAudioQueue(AudioSourceComponent audioSource)
        {
            foreach (var source in audioSource.ActiveListeners.Values)
            {
                source.Stop();
                source.UnqueueConsumedBuffers();

                // Transition STOPPED → INITIAL so that BuffersProcessed
                // resets to 0 for newly queued buffers. Without this,
                // OpenAL marks every buffer on a stopped source as
                // "processed" immediately, causing QueueBuffers to
                // unqueue previous buffers and preventing accumulation
                // past 1 — which starves the pre-buffer gate.
                source.Rewind();
            }

            _totalAudioDurationSubmittedTicks = 0;
            _totalAudioBuffersSubmitted = 0;
            _submittedSampleCounts.Clear();
            _processedSampleCount = 0;
            _firstAudioPts = long.MinValue;
            _driftEwmaSeeded = false;
            _driftEwmaTicks = 0;
            _telemetryCatchupDrops = 0;
            _lastPresentedDriftTicks = 0;
            _driftIntervalSamples = 0;
            _driftIntervalMinTicks = long.MaxValue;
            _driftIntervalMaxTicks = long.MinValue;

            // Clear _wasAudioPlaying so that Phase 3 does not see a
            // Playing→Stopped transition caused by the flush itself and
            // count it as an audible underrun.  The flush is an intentional
            // stop (format change), not a buffer starvation event.
            _wasAudioPlaying = false;
        }

        /// <summary>
        /// Returns the minimum number of playable (queued − processed) buffers
        /// across all active OpenAL listener sources. Returns 0 when no sources
        /// are active.
        /// </summary>
        private static int GetPlayableAudioBuffers(AudioSourceComponent? audioSource)
        {
            if (audioSource is null || audioSource.ActiveListeners.IsEmpty)
                return 0;

            int minQueued = int.MaxValue;
            foreach (var source in audioSource.ActiveListeners.Values)
            {
                int playable = Math.Max(0, source.BuffersQueued - source.BuffersProcessed);
                minQueued = Math.Min(minQueued, playable);
            }

            return minQueued == int.MaxValue ? 0 : minQueued;
        }

        /// <summary>
        /// Estimates the remaining queued audio duration from the playable buffer
        /// count and the average duration per submitted buffer.
        /// Used only for the drain-stop condition and playback-start gate.
        /// </summary>
        private long GetEstimatedQueuedAudioDurationTicks(AudioSourceComponent? audioSource)
        {
            int playable = GetPlayableAudioBuffers(audioSource);
            if (playable <= 0 || _totalAudioBuffersSubmitted <= 0)
                return 0;

            long avgTicksPerBuffer = _totalAudioDurationSubmittedTicks / _totalAudioBuffersSubmitted;
            return playable * avgTicksPerBuffer;
        }

        // ═══════════════════════════════════════════════════════════════
        // Audio Source Configuration
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Disables OpenAL auto-play and configures the audio source for manual
        /// buffer management. Also refreshes the primary source subscription for
        /// the hardware clock and emits a periodic diagnostic log.
        /// </summary>
        private void SuppressAutoPlayOnAudioSources(AudioSourceComponent? audioSource)
        {
            if (audioSource is null)
                return;

            if (!audioSource.ExternalBufferManagement)
                audioSource.ExternalBufferManagement = true;

            if (audioSource.MaxStreamingBuffers < TargetAudioSourceMaxStreamingBuffers)
                audioSource.MaxStreamingBuffers = TargetAudioSourceMaxStreamingBuffers;

            // Refresh primary source for clock tracking.
            UpdatePrimaryAudioSource(audioSource);

            // Periodic full-state diagnostic.
            long nowTicks = GetEngineTimeTicks();
            if (nowTicks - _lastListenerDiagnosticTicks >= TimeSpan.TicksPerSecond * 5)
            {
                _lastListenerDiagnosticTicks = nowTicks;
                int count = audioSource.ActiveListeners.Count;

                bool anyPlaying = false;
                int buffersQueued = 0, buffersProcessed = 0;
                foreach (var src in audioSource.ActiveListeners.Values)
                {
                    if (src.IsPlaying) anyPlaying = true;
                    buffersQueued    = src.BuffersQueued;
                    buffersProcessed = src.BuffersProcessed;
                    break; // just the primary
                }

                long clockTicks = GetAudioClock();
                long queuedMs   = GetEstimatedQueuedAudioDurationTicks(audioSource) / TimeSpan.TicksPerMillisecond;

                string stateMsg =
                    $"[AV Audio State] listeners={count} playing={anyPlaying} " +
                    $"ExternalBufMgmt={audioSource.ExternalBufferManagement} MaxBufs={audioSource.MaxStreamingBuffers} | " +
                    $"primarySrc={(_primaryAudioSource != null ? "set" : "null")} " +
                    $"rate={_primaryAudioSampleRate} " +
                    $"alQueued={buffersQueued} alProcessed={buffersProcessed} | " +
                    $"firstPts={(_firstAudioPts == long.MinValue ? "unseeded" : _firstAudioPts.ToString())} " +
                    $"processedSamples={_processedSampleCount} " +
                    $"submittedBufs={_totalAudioBuffersSubmitted} " +
                    $"clock={clockTicks / TimeSpan.TicksPerMillisecond}ms " +
                    $"queuedMs={queuedMs}ms";

                if (count == 0)
                    Debug.UIWarning("[AV Audio] 0 active listeners — audio will not play until an " +
                                    "AudioListenerComponent is in range.\n" + stateMsg);
                else
                    Debug.Out(stateMsg);
            }

            foreach (var source in audioSource.ActiveListeners.Values)
            {
                if (source.AutoPlayOnQueue)
                    source.AutoPlayOnQueue = false;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Audio Format Conversion — Interleaved → PCM16
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Dispatches the decoded audio frame to the appropriate format converter,
        /// producing an interleaved stereo PCM16 buffer for OpenAL.
        /// </summary>
        private static bool TryConvertToOpenAlPcm16(DecodedAudioFrame frame, out short[] pcm16, out bool stereo)
        {
            pcm16 = [];
            stereo = false;

            int inputChannels = Math.Max(1, frame.ChannelCount);
            byte[] raw = frame.InterleavedData.ToArray();
            if (raw.Length == 0 || frame.SampleRate <= 0)
                return false;

            int inputBytesPerSample = frame.SampleFormat switch
            {
                AudioSampleFormat.S16 => sizeof(short),
                AudioSampleFormat.S32 => sizeof(int),
                AudioSampleFormat.F32 => sizeof(float),
                AudioSampleFormat.F64 => sizeof(double),
                _ => 0
            };

            if (inputBytesPerSample <= 0)
                return false;

            int inputFrameSize = inputBytesPerSample * inputChannels;
            if (inputFrameSize <= 0)
                return false;

            int inputFrameCount = raw.Length / inputFrameSize;
            if (inputFrameCount <= 0)
                return false;

            // Always output stereo (2 channels) for OpenAL.
            const int outputChannels = 2;
            stereo = true;
            pcm16 = new short[inputFrameCount * outputChannels];

            switch (frame.SampleFormat)
            {
                case AudioSampleFormat.S16:
                    ConvertS16InterleavedToPcm16(raw, inputChannels, outputChannels, inputFrameCount, pcm16);
                    break;
                case AudioSampleFormat.S32:
                    ConvertS32InterleavedToPcm16(raw, inputChannels, outputChannels, inputFrameCount, pcm16);
                    break;
                case AudioSampleFormat.F32:
                    ConvertF32InterleavedToPcm16(raw, inputChannels, outputChannels, inputFrameCount, pcm16);
                    break;
                case AudioSampleFormat.F64:
                    ConvertF64InterleavedToPcm16(raw, inputChannels, outputChannels, inputFrameCount, pcm16);
                    break;
                default:
                    return false;
            }

            return true;
        }

        private static void ConvertS16InterleavedToPcm16(byte[] raw, int inputChannels, int outputChannels, int frameCount, short[] dest)
        {
            short[] src = new short[raw.Length / sizeof(short)];
            Buffer.BlockCopy(raw, 0, src, 0, raw.Length);

            for (int i = 0; i < frameCount; i++)
            {
                int inBase = i * inputChannels;
                int outBase = i * outputChannels;
                short left = src[inBase];
                short right = inputChannels > 1 ? src[inBase + 1] : left;
                dest[outBase] = left;
                dest[outBase + 1] = right;
            }
        }

        private static void ConvertS32InterleavedToPcm16(byte[] raw, int inputChannels, int outputChannels, int frameCount, short[] dest)
        {
            int[] src = new int[raw.Length / sizeof(int)];
            Buffer.BlockCopy(raw, 0, src, 0, raw.Length);

            for (int i = 0; i < frameCount; i++)
            {
                int inBase = i * inputChannels;
                int outBase = i * outputChannels;
                short left = (short)(src[inBase] >> 16);
                short right = inputChannels > 1 ? (short)(src[inBase + 1] >> 16) : left;
                dest[outBase] = left;
                dest[outBase + 1] = right;
            }
        }

        private static void ConvertF32InterleavedToPcm16(byte[] raw, int inputChannels, int outputChannels, int frameCount, short[] dest)
        {
            float[] src = new float[raw.Length / sizeof(float)];
            Buffer.BlockCopy(raw, 0, src, 0, raw.Length);

            for (int i = 0; i < frameCount; i++)
            {
                int inBase = i * inputChannels;
                int outBase = i * outputChannels;
                short left = FloatToPcm16(src[inBase]);
                short right = inputChannels > 1 ? FloatToPcm16(src[inBase + 1]) : left;
                dest[outBase] = left;
                dest[outBase + 1] = right;
            }
        }

        private static void ConvertF64InterleavedToPcm16(byte[] raw, int inputChannels, int outputChannels, int frameCount, short[] dest)
        {
            double[] src = new double[raw.Length / sizeof(double)];
            Buffer.BlockCopy(raw, 0, src, 0, raw.Length);

            for (int i = 0; i < frameCount; i++)
            {
                int inBase = i * inputChannels;
                int outBase = i * outputChannels;
                short left = FloatToPcm16((float)src[inBase]);
                short right = inputChannels > 1 ? FloatToPcm16((float)src[inBase + 1]) : left;
                dest[outBase] = left;
                dest[outBase + 1] = right;
            }
        }

        private static short FloatToPcm16(float sample)
        {
            float clamped = Math.Clamp(sample, -1.0f, 1.0f);
            return (short)MathF.Round(clamped * short.MaxValue);
        }

        // ═══════════════════════════════════════════════════════════════
        // Audio Duration Estimation (for drain-stop / pre-buffer gate)
        // ═══════════════════════════════════════════════════════════════

        private static long EstimateAudioDurationTicks(int sampleRate, int channelCount, int bytesPerSample, int byteLength)
        {
            channelCount = Math.Max(1, channelCount);
            int bytesPerFrame = bytesPerSample * channelCount;
            if (bytesPerFrame <= 0 || sampleRate <= 0)
                return 0;

            int sampleFrames = byteLength / bytesPerFrame;
            return (long)(sampleFrames * (double)TimeSpan.TicksPerSecond / sampleRate);
        }
    }
}
