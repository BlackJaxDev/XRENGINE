using NAudio.Wave;
using System.Diagnostics;
using System.Numerics;

namespace XREngine.Audio
{
    /// <summary>
    /// NAudio-based implementation of <see cref="IAudioTransport"/>.
    /// Uses a managed software mixer (<see cref="NAudioMixer"/>) and a
    /// <see cref="WaveOutEvent"/> for audio output on Windows.
    /// <para>
    /// This transport is an opt-in alternative to <see cref="OpenALTransport"/>.
    /// It eliminates the dependency on OpenAL for audio playback while
    /// supporting the same <see cref="IAudioTransport"/> contract.
    /// </para>
    /// <para>
    /// <b>Design notes:</b>
    /// <list type="bullet">
    ///   <item>Source and buffer state is fully managed in-process via <see cref="NAudioMixer"/>.</item>
    ///   <item>Mixing runs on the NAudio playback thread; state mutations are lock-protected.</item>
    ///   <item>The transport is functional for source/buffer state operations even before
    ///     <see cref="Open"/> is called (or if Open fails). This aids unit testing.</item>
    ///   <item><see cref="WaveOutEvent"/> is the default output device. For cross-platform
    ///     support, <c>NAudio.Sdl2.SdlOut</c> could replace it.</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class NAudioTransport : IAudioTransport
    {
        private readonly NAudioMixer _mixer;
        private IWavePlayer? _output;
        private bool _disposed;

        // Handle ID counters (start at 1; 0 = invalid)
        private uint _nextSourceId = 1;
        private uint _nextBufferId = 1;

        // --- IAudioTransport: Device state ---

        /// <inheritdoc />
        public string? DeviceName { get; private set; }

        /// <inheritdoc />
        public int SampleRate { get; }

        /// <inheritdoc />
        public bool IsOpen { get; private set; }

        /// <summary>Number of output channels (default: 2 for stereo).</summary>
        public int OutputChannels { get; }

        /// <summary>
        /// Exposes the internal mixer for unit testing.
        /// Allows tests to call <see cref="NAudioMixer.Read(float[], int, int)"/>
        /// directly to verify mixed output without requiring a real audio device.
        /// </summary>
        internal NAudioMixer Mixer => _mixer;

        /// <summary>
        /// Creates a new NAudio transport with the specified sample rate and channel count.
        /// The transport is immediately usable for source/buffer operations.
        /// Call <see cref="Open"/> to initialize the output device and begin playback.
        /// </summary>
        /// <param name="sampleRate">Output sample rate in Hz (default: 44100).</param>
        /// <param name="channels">Number of output channels (default: 2 for stereo).</param>
        public NAudioTransport(int sampleRate = 44100, int channels = 2)
        {
            SampleRate = sampleRate;
            OutputChannels = channels;
            _mixer = new NAudioMixer(sampleRate, channels);
        }

        // --- IAudioTransport: Device ---

        /// <inheritdoc />
        public void Open(string? deviceName = null)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsOpen)
                return;

            DeviceName = deviceName;

            try
            {
                _output = new WaveOutEvent();
                _output.Init(_mixer);
                _output.Play();
                IsOpen = true;
                Debug.WriteLine($"[NAudioTransport] Opened audio output (WaveOutEvent, {SampleRate}Hz, {OutputChannels}ch).");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NAudioTransport] Failed to open audio output: {ex.Message}");
                _output?.Dispose();
                _output = null;
                // Transport remains usable for source/buffer operations even without output
            }
        }

        /// <inheritdoc />
        public void Close()
        {
            if (!IsOpen && _output == null)
                return;

            _output?.Stop();
            _output?.Dispose();
            _output = null;
            IsOpen = false;
            Debug.WriteLine("[NAudioTransport] Closed audio output.");
        }

        // --- IAudioTransport: Listener ---

        /// <inheritdoc />
        public void SetListenerPosition(Vector3 position) => _mixer.SetListenerPosition(position);

        /// <inheritdoc />
        public void SetListenerVelocity(Vector3 velocity) => _mixer.SetListenerVelocity(velocity);

        /// <inheritdoc />
        public void SetListenerOrientation(Vector3 forward, Vector3 up) => _mixer.SetListenerOrientation(forward, up);

        /// <inheritdoc />
        public void SetListenerGain(float gain) => _mixer.SetListenerGain(gain);

        // --- IAudioTransport: Source lifecycle ---

        /// <inheritdoc />
        public AudioSourceHandle CreateSource()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            uint id = _nextSourceId++;
            _mixer.AddSource(id);
            return new AudioSourceHandle(id);
        }

        /// <inheritdoc />
        public void DestroySource(AudioSourceHandle source)
        {
            if (!source.IsValid)
                return;

            _mixer.Stop(source.Id);
            _mixer.RemoveSource(source.Id);
        }

        // --- IAudioTransport: Buffer lifecycle ---

        /// <inheritdoc />
        public AudioBufferHandle CreateBuffer()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            uint id = _nextBufferId++;
            _mixer.AddBuffer(id);
            return new AudioBufferHandle(id);
        }

        /// <inheritdoc />
        public void DestroyBuffer(AudioBufferHandle buffer)
        {
            if (!buffer.IsValid)
                return;
            _mixer.RemoveBuffer(buffer.Id);
        }

        /// <inheritdoc />
        public void UploadBufferData(AudioBufferHandle buffer, ReadOnlySpan<byte> pcm, int frequency, int channels, SampleFormat format)
        {
            if (!buffer.IsValid)
                return;
            _mixer.UploadBufferData(buffer.Id, pcm, frequency, channels, format);
        }

        // --- IAudioTransport: Playback ---

        /// <inheritdoc />
        public void Play(AudioSourceHandle source) => _mixer.Play(source.Id);

        /// <inheritdoc />
        public void Stop(AudioSourceHandle source) => _mixer.Stop(source.Id);

        /// <inheritdoc />
        public void Pause(AudioSourceHandle source) => _mixer.Pause(source.Id);

        /// <inheritdoc />
        public void SetSourceBuffer(AudioSourceHandle source, AudioBufferHandle buffer)
            => _mixer.SetSourceBuffer(source.Id, buffer.Id);

        /// <inheritdoc />
        public void QueueBuffers(AudioSourceHandle source, ReadOnlySpan<AudioBufferHandle> buffers)
        {
            Span<uint> ids = stackalloc uint[buffers.Length];
            for (int i = 0; i < buffers.Length; i++)
                ids[i] = buffers[i].Id;
            _mixer.QueueBuffers(source.Id, ids);
        }

        /// <inheritdoc />
        public int UnqueueProcessedBuffers(AudioSourceHandle source, Span<AudioBufferHandle> output)
        {
            Span<uint> ids = stackalloc uint[output.Length];
            int count = _mixer.UnqueueProcessedBuffers(source.Id, ids);
            for (int i = 0; i < count; i++)
                output[i] = new AudioBufferHandle(ids[i]);
            return count;
        }

        // --- IAudioTransport: Source properties ---

        /// <inheritdoc />
        public void SetSourcePosition(AudioSourceHandle source, Vector3 position)
            => _mixer.SetSourcePosition(source.Id, position);

        /// <inheritdoc />
        public void SetSourceVelocity(AudioSourceHandle source, Vector3 velocity)
            => _mixer.SetSourceVelocity(source.Id, velocity);

        /// <inheritdoc />
        public void SetSourceGain(AudioSourceHandle source, float gain)
            => _mixer.SetSourceGain(source.Id, gain);

        /// <inheritdoc />
        public void SetSourcePitch(AudioSourceHandle source, float pitch)
            => _mixer.SetSourcePitch(source.Id, pitch);

        /// <inheritdoc />
        public void SetSourceLooping(AudioSourceHandle source, bool loop)
            => _mixer.SetSourceLooping(source.Id, loop);

        /// <inheritdoc />
        public bool IsSourcePlaying(AudioSourceHandle source) => _mixer.IsPlaying(source.Id);

        // --- IAudioTransport: Capture ---

        /// <inheritdoc />
        public IAudioCaptureDevice? OpenCaptureDevice(string? device, int sampleRate, SampleFormat format, int bufferSize)
        {
            // NAudio capture support deferred to a later phase.
            return null;
        }

        // --- IDisposable ---

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Close();
            GC.SuppressFinalize(this);
        }
    }
}
