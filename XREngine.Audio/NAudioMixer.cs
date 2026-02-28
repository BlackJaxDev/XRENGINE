using NAudio.Wave;
using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Audio
{
    /// <summary>
    /// Managed software audio mixer for the NAudio transport.
    /// Implements <see cref="ISampleProvider"/> so it can be fed into any NAudio
    /// <see cref="IWavePlayer"/> output device (e.g. <c>WaveOutEvent</c>).
    /// <para>
    /// Tracks sources and buffers internally, mixes all playing sources in the
    /// <see cref="Read"/> callback, applies per-source gain/pitch and listener gain.
    /// Thread-safe: the audio callback thread calls <see cref="Read"/> while the
    /// main thread mutates source/buffer state.
    /// </para>
    /// </summary>
    internal sealed class NAudioMixer(int sampleRate, int channels) : ISampleProvider
    {
        #region Internal types

        /// <summary>Stores uploaded PCM data converted to float.</summary>
        internal sealed class MixerBuffer
        {
            /// <summary>Interleaved float PCM samples.</summary>
            public readonly float[] Samples;

            /// <summary>Number of channels in this buffer.</summary>
            public readonly int Channels;

            /// <summary>Sample rate of this buffer's data.</summary>
            public readonly int BufferSampleRate;

            /// <summary>Number of sample frames (samples per channel).</summary>
            public int FrameCount => Samples.Length / Math.Max(Channels, 1);

            public MixerBuffer(float[] samples, int channels, int sampleRate)
            {
                Samples = samples;
                Channels = channels;
                BufferSampleRate = sampleRate;
            }
        }

        internal enum PlaybackState { Stopped, Playing, Paused }

        /// <summary>Per-source playback state managed by the mixer.</summary>
        internal sealed class MixerSource
        {
            public PlaybackState State = PlaybackState.Stopped;

            // --- Static buffer binding (via SetSourceBuffer) ---
            public uint BoundBufferId;

            // --- Streaming queue ---
            public readonly Queue<uint> QueuedBuffers = new();
            public readonly Queue<uint> ProcessedBuffers = new();

            // --- Playback position ---
            /// <summary>Fractional frame index for pitch-adjusted playback.</summary>
            public double PlaybackPosition;

            // --- Per-source properties ---
            public float Gain = 1.0f;
            public float Pitch = 1.0f;
            public bool Looping;
            public Vector3 Position;
            public Vector3 Velocity;

            /// <summary>Whether this source is in streaming (queue) mode.</summary>
            public bool IsStreaming => QueuedBuffers.Count > 0 || ProcessedBuffers.Count > 0;

            /// <summary>Gets the active buffer ID (front of queue if streaming, else bound buffer).</summary>
            public uint CurrentBufferId => IsStreaming && QueuedBuffers.Count > 0
                ? QueuedBuffers.Peek()
                : BoundBufferId;
        }

        #endregion

        private readonly object _lock = new();
        private readonly Dictionary<uint, MixerSource> _sources = new();
        private readonly Dictionary<uint, MixerBuffer> _buffers = new();

        // Listener state
        private float _listenerGain = 1.0f;
        private Vector3 _listenerPosition;
        private Vector3 _listenerVelocity;
        private Vector3 _listenerForward = -Vector3.UnitZ;
        private Vector3 _listenerUp = Vector3.UnitY;

        /// <summary>NAudio wave format for the mixer output.</summary>
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

        /// <summary>Number of output channels.</summary>
        public int OutputChannels { get; } = channels;

        #region Source management

        public void AddSource(uint id)
        {
            lock (_lock) _sources[id] = new MixerSource();
        }

        public void RemoveSource(uint id)
        {
            lock (_lock) _sources.Remove(id);
        }

        public bool HasSource(uint id)
        {
            lock (_lock) return _sources.ContainsKey(id);
        }

        /// <summary>Returns current playback state. For testing.</summary>
        internal PlaybackState GetSourceState(uint id)
        {
            lock (_lock)
                return _sources.TryGetValue(id, out var s) ? s.State : PlaybackState.Stopped;
        }

        #endregion

        #region Buffer management

        public void AddBuffer(uint id)
        {
            lock (_lock) _buffers[id] = new MixerBuffer([], 1, WaveFormat.SampleRate);
        }

        public void RemoveBuffer(uint id)
        {
            lock (_lock) _buffers.Remove(id);
        }

        public void UploadBufferData(uint bufferId, ReadOnlySpan<byte> pcm, int frequency, int channels, SampleFormat format)
        {
            float[] samples = ConvertToFloat(pcm, format);
            lock (_lock)
            {
                _buffers[bufferId] = new MixerBuffer(samples, channels, frequency);
            }
        }

        #endregion

        #region Playback control

        public void Play(uint sourceId)
        {
            lock (_lock)
            {
                if (_sources.TryGetValue(sourceId, out var source))
                {
                    if (source.State == PlaybackState.Stopped)
                        source.PlaybackPosition = 0;
                    source.State = PlaybackState.Playing;
                }
            }
        }

        public void Stop(uint sourceId)
        {
            lock (_lock)
            {
                if (_sources.TryGetValue(sourceId, out var source))
                {
                    source.State = PlaybackState.Stopped;
                    source.PlaybackPosition = 0;
                }
            }
        }

        public void Pause(uint sourceId)
        {
            lock (_lock)
            {
                if (_sources.TryGetValue(sourceId, out var source))
                    source.State = PlaybackState.Paused;
            }
        }

        public bool IsPlaying(uint sourceId)
        {
            lock (_lock)
                return _sources.TryGetValue(sourceId, out var source) && source.State == PlaybackState.Playing;
        }

        #endregion

        #region Source buffer binding / streaming

        public void SetSourceBuffer(uint sourceId, uint bufferId)
        {
            lock (_lock)
            {
                if (_sources.TryGetValue(sourceId, out var source))
                {
                    source.BoundBufferId = bufferId;
                    source.PlaybackPosition = 0;
                    // Setting a static buffer clears streaming state.
                    source.QueuedBuffers.Clear();
                    source.ProcessedBuffers.Clear();
                }
            }
        }

        public void QueueBuffers(uint sourceId, ReadOnlySpan<uint> bufferIds)
        {
            lock (_lock)
            {
                if (_sources.TryGetValue(sourceId, out var source))
                {
                    foreach (var id in bufferIds)
                        source.QueuedBuffers.Enqueue(id);
                }
            }
        }

        public int UnqueueProcessedBuffers(uint sourceId, Span<uint> output)
        {
            lock (_lock)
            {
                if (!_sources.TryGetValue(sourceId, out var source))
                    return 0;

                int count = Math.Min(source.ProcessedBuffers.Count, output.Length);
                for (int i = 0; i < count; i++)
                    output[i] = source.ProcessedBuffers.Dequeue();
                return count;
            }
        }

        #endregion

        #region Source properties

        public void SetSourceGain(uint id, float gain)
        {
            lock (_lock) { if (_sources.TryGetValue(id, out var s)) s.Gain = gain; }
        }

        public void SetSourcePitch(uint id, float pitch)
        {
            lock (_lock) { if (_sources.TryGetValue(id, out var s)) s.Pitch = pitch; }
        }

        public void SetSourceLooping(uint id, bool loop)
        {
            lock (_lock) { if (_sources.TryGetValue(id, out var s)) s.Looping = loop; }
        }

        public void SetSourcePosition(uint id, Vector3 pos)
        {
            lock (_lock) { if (_sources.TryGetValue(id, out var s)) s.Position = pos; }
        }

        public void SetSourceVelocity(uint id, Vector3 vel)
        {
            lock (_lock) { if (_sources.TryGetValue(id, out var s)) s.Velocity = vel; }
        }

        #endregion

        #region Listener

        public void SetListenerGain(float gain)
        {
            lock (_lock) _listenerGain = gain;
        }

        public void SetListenerPosition(Vector3 pos)
        {
            lock (_lock) _listenerPosition = pos;
        }

        public void SetListenerVelocity(Vector3 vel)
        {
            lock (_lock) _listenerVelocity = vel;
        }

        public void SetListenerOrientation(Vector3 fwd, Vector3 up)
        {
            lock (_lock) { _listenerForward = fwd; _listenerUp = up; }
        }

        #endregion

        #region ISampleProvider.Read — mixing core

        /// <summary>
        /// Called by the NAudio output device on its audio thread.
        /// Mixes all playing sources into the output buffer each callback.
        /// </summary>
        public int Read(float[] buffer, int offset, int count)
        {
            // Zero the output region
            Array.Clear(buffer, offset, count);

            float gain;
            lock (_lock)
            {
                gain = _listenerGain;

                foreach (var (_, source) in _sources)
                {
                    if (source.State != PlaybackState.Playing)
                        continue;

                    MixSource(source, buffer, offset, count);
                }
            }

            // Apply listener gain
            if (Math.Abs(gain - 1.0f) > float.Epsilon)
            {
                for (int i = offset; i < offset + count; i++)
                    buffer[i] *= gain;
            }

            return count; // Always return full count (silence-pad if nothing playing)
        }

        /// <summary>
        /// Mixes a single playing source into the output buffer.
        /// Handles pitch-adjusted playback, looping, and buffer queue advancement.
        /// </summary>
        private void MixSource(MixerSource source, float[] output, int offset, int count)
        {
            int outChannels = OutputChannels;
            int framesNeeded = count / outChannels;
            int framesWritten = 0;

            while (framesWritten < framesNeeded)
            {
                uint bufferId = source.CurrentBufferId;
                if (bufferId == 0 || !_buffers.TryGetValue(bufferId, out var buf))
                {
                    // No buffer bound — stop the source
                    source.State = PlaybackState.Stopped;
                    break;
                }

                int bufFrames = buf.FrameCount;
                int bufChannels = buf.Channels;
                float sourceGain = source.Gain;
                float pitch = Math.Max(source.Pitch, 0.01f); // Clamp to avoid zero/negative advancement

                while (framesWritten < framesNeeded)
                {
                    int frameIdx = (int)source.PlaybackPosition;

                    if (frameIdx >= bufFrames)
                    {
                        // End of current buffer
                        if (source.Looping && !source.IsStreaming)
                        {
                            // Static looping — wrap around
                            source.PlaybackPosition = 0;
                            frameIdx = 0;
                        }
                        else
                        {
                            // Streaming or non-looping — advance to next buffer or stop
                            if (!AdvanceToNextBuffer(source))
                            {
                                source.State = PlaybackState.Stopped;
                                return;
                            }
                            source.PlaybackPosition = 0;
                            break; // Re-enter outer loop with new buffer
                        }
                    }

                    // Read sample from buffer and mix (additive) into output
                    int outIdx = offset + framesWritten * outChannels;
                    int srcIdx = frameIdx * bufChannels;

                    for (int ch = 0; ch < outChannels; ch++)
                    {
                        // Map output channel to buffer channel (mono → duplicate to all channels)
                        int srcCh = ch < bufChannels ? ch : 0;
                        float sample = buf.Samples[srcIdx + srcCh] * sourceGain;
                        output[outIdx + ch] += sample;
                    }

                    framesWritten++;
                    source.PlaybackPosition += pitch;
                }
            }
        }

        /// <summary>
        /// Advances a streaming source to the next queued buffer.
        /// Moves the finished buffer from the queue to the processed list.
        /// Returns false if no more buffers are available.
        /// </summary>
        private static bool AdvanceToNextBuffer(MixerSource source)
        {
            if (source.QueuedBuffers.Count == 0)
                return false;

            uint finished = source.QueuedBuffers.Dequeue();
            source.ProcessedBuffers.Enqueue(finished);

            // Return true if another buffer is ready
            return source.QueuedBuffers.Count > 0;
        }

        #endregion

        #region PCM conversion

        /// <summary>
        /// Converts raw PCM bytes to float samples.
        /// All buffer data is stored as float internally for efficient mixing.
        /// </summary>
        internal static float[] ConvertToFloat(ReadOnlySpan<byte> pcm, SampleFormat format)
        {
            switch (format)
            {
                case SampleFormat.Float:
                {
                    int sampleCount = pcm.Length / sizeof(float);
                    float[] result = new float[sampleCount];
                    MemoryMarshal.Cast<byte, float>(pcm).CopyTo(result);
                    return result;
                }
                case SampleFormat.Short:
                {
                    int sampleCount = pcm.Length / sizeof(short);
                    float[] result = new float[sampleCount];
                    var shortSpan = MemoryMarshal.Cast<byte, short>(pcm);
                    for (int i = 0; i < sampleCount; i++)
                        result[i] = shortSpan[i] / 32768f;
                    return result;
                }
                case SampleFormat.Byte:
                {
                    float[] result = new float[pcm.Length];
                    for (int i = 0; i < pcm.Length; i++)
                        result[i] = (pcm[i] - 128) / 128f; // Unsigned byte → signed float [-1, 1)
                    return result;
                }
                default:
                    return [];
            }
        }

        #endregion
    }
}
