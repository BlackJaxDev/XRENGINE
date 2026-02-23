using System;
using XREngine.Components;
using XREngine.Rendering.VideoStreaming;

namespace XREngine.Rendering.UI
{
    // ═══════════════════════════════════════════════════════════════════
    // UIVideoComponent — Audio Submission, Clock & Adaptive Pitch
    //
    // Handles OpenAL audio buffer submission (with PCM format conversion),
    // a software audio clock for A/V sync, queue-depth estimation, and
    // an adaptive catch-up pitch system that speeds up playback when
    // buffered latency exceeds the target.
    // ═══════════════════════════════════════════════════════════════════

    public partial class UIVideoComponent
    {
        // ═══════════════════════════════════════════════════════════════
        // Audio Frame — OpenAL Submission
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Converts a decoded audio frame to PCM16, handles format-change
        /// detection (which requires flushing the OpenAL queue), enqueues
        /// the buffer, and advances the audio clock seed.
        /// </summary>
        /// <returns><c>true</c> if the frame was successfully queued.</returns>
        private bool SubmitDecodedAudioFrame(DecodedAudioFrame frame, AudioSourceComponent? audioSource)
        {
            if (audioSource is null || frame.InterleavedData.IsEmpty)
                return false;

            if (!TryConvertToOpenAlPcm16(frame, out short[] pcm16, out bool stereo))
                return false;

            if (pcm16.Length == 0)
                return false;

            int sampleRate = Math.Clamp(frame.SampleRate, 8000, 192000);

            // OpenAL requires all queued buffers on a source to share the
            // same format (sample rate + channel count).  HLS segment
            // transitions can change the audio format, causing
            // alSourceQueueBuffers to return AL_INVALID_VALUE.  Detect the
            // change and flush the queue first.
            if (_lastSubmittedAudioSampleRate != 0 &&
                (_lastSubmittedAudioSampleRate != sampleRate || _lastSubmittedAudioStereo != stereo))
            {
                foreach (var source in audioSource.ActiveListeners.Values)
                {
                    source.Stop();
                    source.UnqueueConsumedBuffers();
                }

                _totalAudioDurationSubmittedTicks = 0;
                _totalAudioBuffersSubmitted = 0;
            }
            _lastSubmittedAudioSampleRate = sampleRate;
            _lastSubmittedAudioStereo = stereo;

            audioSource.EnqueueStreamingBuffers(sampleRate, stereo, pcm16);

            // Track PTS and seed the audio clock from the first audio frame
            // so the video drain loop has a reference point for A/V sync.
            _lastPresentedAudioPts = frame.PresentationTimestampTicks;
            if (_audioClockTicks == long.MinValue && frame.PresentationTimestampTicks > 0)
                _audioClockTicks = frame.PresentationTimestampTicks;

            _audioClockLastEngineTicks = GetEngineTimeTicks();
            long frameDurationTicks = EstimateAudioDurationTicks(frame.SampleRate, stereo ? 2 : 1, sizeof(short), pcm16.Length * sizeof(short));
            _totalAudioDurationSubmittedTicks += frameDurationTicks;
            _totalAudioBuffersSubmitted++;
            _telemetryAudioFramesSubmitted++;
            return true;
        }

        // ═══════════════════════════════════════════════════════════════
        // Audio Format Conversion — Interleaved → PCM16
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Dispatches the decoded audio frame to the appropriate format-specific
        /// converter, producing a stereo PCM16 buffer suitable for OpenAL.
        /// Supports S16, S32, F32, and F64 input formats.
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
            int outputChannels = 2;
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

        /// <summary>
        /// Converts signed 16-bit interleaved audio to stereo PCM16.
        /// Mono input is duplicated to both channels.
        /// </summary>
        private static void ConvertS16InterleavedToPcm16(byte[] raw, int inputChannels, int outputChannels, int frameCount, short[] dest)
        {
            short[] src = new short[raw.Length / sizeof(short)];
            Buffer.BlockCopy(raw, 0, src, 0, raw.Length);

            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                int inBase = frameIndex * inputChannels;
                int outBase = frameIndex * outputChannels;

                if (outputChannels == 1)
                {
                    dest[outBase] = src[inBase];
                }
                else
                {
                    short left = src[inBase];
                    short right = inputChannels > 1 ? src[inBase + 1] : left;
                    dest[outBase] = left;
                    dest[outBase + 1] = right;
                }
            }
        }

        /// <summary>
        /// Converts signed 32-bit interleaved audio to stereo PCM16.
        /// Truncates by right-shifting 16 bits (keeps the upper 16 bits of precision).
        /// </summary>
        private static void ConvertS32InterleavedToPcm16(byte[] raw, int inputChannels, int outputChannels, int frameCount, short[] dest)
        {
            int[] src = new int[raw.Length / sizeof(int)];
            Buffer.BlockCopy(raw, 0, src, 0, raw.Length);

            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                int inBase = frameIndex * inputChannels;
                int outBase = frameIndex * outputChannels;

                if (outputChannels == 1)
                {
                    dest[outBase] = (short)(src[inBase] >> 16);
                }
                else
                {
                    short left = (short)(src[inBase] >> 16);
                    short right = inputChannels > 1 ? (short)(src[inBase + 1] >> 16) : left;
                    dest[outBase] = left;
                    dest[outBase + 1] = right;
                }
            }
        }

        /// <summary>
        /// Converts 32-bit float interleaved audio ([-1.0, 1.0]) to stereo PCM16.
        /// </summary>
        private static void ConvertF32InterleavedToPcm16(byte[] raw, int inputChannels, int outputChannels, int frameCount, short[] dest)
        {
            float[] src = new float[raw.Length / sizeof(float)];
            Buffer.BlockCopy(raw, 0, src, 0, raw.Length);

            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                int inBase = frameIndex * inputChannels;
                int outBase = frameIndex * outputChannels;

                if (outputChannels == 1)
                {
                    dest[outBase] = FloatToPcm16(src[inBase]);
                }
                else
                {
                    short left = FloatToPcm16(src[inBase]);
                    short right = inputChannels > 1 ? FloatToPcm16(src[inBase + 1]) : left;
                    dest[outBase] = left;
                    dest[outBase + 1] = right;
                }
            }
        }

        /// <summary>
        /// Converts 64-bit float (double) interleaved audio to stereo PCM16.
        /// Narrows to float before clamping to [-1.0, 1.0].
        /// </summary>
        private static void ConvertF64InterleavedToPcm16(byte[] raw, int inputChannels, int outputChannels, int frameCount, short[] dest)
        {
            double[] src = new double[raw.Length / sizeof(double)];
            Buffer.BlockCopy(raw, 0, src, 0, raw.Length);

            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                int inBase = frameIndex * inputChannels;
                int outBase = frameIndex * outputChannels;

                if (outputChannels == 1)
                {
                    dest[outBase] = FloatToPcm16((float)src[inBase]);
                }
                else
                {
                    short left = FloatToPcm16((float)src[inBase]);
                    short right = inputChannels > 1 ? FloatToPcm16((float)src[inBase + 1]) : left;
                    dest[outBase] = left;
                    dest[outBase + 1] = right;
                }
            }
        }

        /// <summary>
        /// Clamps a float sample to [-1.0, 1.0] and scales to <see cref="short.MaxValue"/>.
        /// </summary>
        private static short FloatToPcm16(float sample)
        {
            float clamped = Math.Clamp(sample, -1.0f, 1.0f);
            return (short)MathF.Round(clamped * short.MaxValue);
        }

        // ═══════════════════════════════════════════════════════════════
        // Audio Clock — Software A/V Sync
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Advances the software audio clock by wall-clock delta scaled by
        /// the current pitch. Only advances while at least one OpenAL source
        /// is actively playing.
        /// </summary>
        private void UpdateAudioClock(AudioSourceComponent? audioSource)
        {
            if (_audioClockTicks == long.MinValue || _audioClockLastEngineTicks == long.MinValue)
                return;

            bool audioPlaying = audioSource?.ActiveListeners.Values.Any(static source => source.IsPlaying) == true;
            if (!audioPlaying)
                return;

            long nowTicks = GetEngineTimeTicks();
            if (nowTicks <= _audioClockLastEngineTicks)
                return;

            long elapsedTicks = nowTicks - _audioClockLastEngineTicks;
            _audioClockLastEngineTicks = nowTicks;
            // Advance the audio clock proportional to the current pitch so that
            // video sync tracks the actual audio playback position.
            _audioClockTicks += (long)(elapsedTicks * _currentPitch);
        }

        /// <summary>
        /// Returns the best available audio clock value for video sync.
        /// Falls back to the last submitted audio PTS if the clock hasn't
        /// been seeded yet.
        /// </summary>
        private long GetAudioClockForVideoSync()
        {
            return _audioClockTicks != long.MinValue
                ? _audioClockTicks
                : _lastPresentedAudioPts > 0
                    ? _lastPresentedAudioPts
                    : 0;
        }

        // ═══════════════════════════════════════════════════════════════
        // Audio Queue Depth Estimation
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Returns the minimum number of playable (queued − processed) buffers
        /// across all active OpenAL listener sources. Returns 0 when no
        /// sources are active.
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
        /// Estimates the remaining queued audio duration from the actual OpenAL
        /// playable buffer count and the average duration per buffer. This is
        /// drift-free because it is recomputed fresh each call instead of relying
        /// on an accumulator that subtracts wall-clock time.
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
        // Adaptive Catch-Up Pitch
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Adjusts audio playback pitch to gradually catch up when the buffered
        /// audio latency is significantly above the target. Uses hysteresis to
        /// avoid oscillation: catch-up engages above <see cref="CatchUpEngageLatencyTicks"/>
        /// and disengages once latency falls to <see cref="TargetPlaybackLatencyTicks"/>.
        /// </summary>
        private void UpdateCatchUpPitch(AudioSourceComponent? audioSource)
        {
            long queuedTicks = GetEstimatedQueuedAudioDurationTicks(audioSource);
            _playbackLatencyMs = queuedTicks / (double)TimeSpan.TicksPerMillisecond;

            bool audioPlaying = audioSource?.ActiveListeners.Values.Any(static s => s.IsPlaying) == true;
            if (!audioPlaying || queuedTicks <= 0)
            {
                ApplyPitch(audioSource, 1.0f);
                return;
            }

            float targetPitch;
            if (queuedTicks >= CatchUpEngageLatencyTicks)
            {
                // Linearly ramp pitch from 1.0 at the engage threshold to
                // CatchUpMaxPitch at CatchUpMaxLatencyTicks.
                long excess = queuedTicks - TargetPlaybackLatencyTicks;
                long rampRange = CatchUpMaxLatencyTicks - TargetPlaybackLatencyTicks;
                float ratio = Math.Clamp((float)excess / rampRange, 0f, 1f);
                targetPitch = 1.0f + ratio * (CatchUpMaxPitch - 1.0f);
            }
            else if (_currentPitch > 1.0f && queuedTicks > TargetPlaybackLatencyTicks)
            {
                // Still catching up (hysteresis) — keep current pitch until
                // we reach the target.
                targetPitch = _currentPitch;
            }
            else
            {
                targetPitch = 1.0f;
            }

            ApplyPitch(audioSource, targetPitch);
        }

        /// <summary>
        /// Sets the pitch on all active OpenAL sources. Snaps near-unity
        /// values to exactly 1.0 to avoid permanent micro-speedup.
        /// </summary>
        private void ApplyPitch(AudioSourceComponent? audioSource, float pitch)
        {
            // Snap near-unity values to exactly 1.0 to avoid permanent micro-speedup.
            if (MathF.Abs(pitch - 1.0f) < 0.002f)
                pitch = 1.0f;

            _currentPitch = pitch;

            if (audioSource is null)
                return;

            foreach (var source in audioSource.ActiveListeners.Values)
            {
                if (MathF.Abs(source.Pitch - pitch) > 0.001f)
                    source.Pitch = pitch;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Audio Source Configuration Helpers
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Disables OpenAL auto-play on all active listener sources so that
        /// <see cref="DrainStreamingFramesOnMainThread"/> controls when playback
        /// begins (after pre-buffering enough audio). Also sets
        /// <see cref="AudioSourceComponent.ExternalBufferManagement"/> to prevent
        /// the update-thread tick from concurrently dequeuing buffers.
        /// </summary>
        private static void SuppressAutoPlayOnAudioSources(AudioSourceComponent? audioSource)
        {
            if (audioSource is null)
                return;

            if (!audioSource.ExternalBufferManagement)
                audioSource.ExternalBufferManagement = true;

            if (audioSource.MaxStreamingBuffers < TargetAudioSourceMaxStreamingBuffers)
                audioSource.MaxStreamingBuffers = TargetAudioSourceMaxStreamingBuffers;

            foreach (var source in audioSource.ActiveListeners.Values)
            {
                if (source.AutoPlayOnQueue)
                    source.AutoPlayOnQueue = false;
            }
        }

        /// <summary>
        /// Estimates the duration (in .NET ticks) of an audio buffer from its
        /// sample rate, channel count, bytes-per-sample, and total byte length.
        /// </summary>
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
