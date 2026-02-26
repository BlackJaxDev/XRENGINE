using System.Numerics;

namespace XREngine.Audio
{
    /// <summary>
    /// Abstraction over the audio output device and source/buffer handle management.
    /// Implementations: <c>OpenALTransport</c>, <c>NAudioTransport</c> (future), etc.
    /// <para>
    /// The transport layer owns the hardware output device, source handle lifecycle,
    /// buffer upload/playback mechanics, and listener spatial state.
    /// </para>
    /// </summary>
    public interface IAudioTransport : IDisposable
    {
        // --- Device ---

        /// <summary>Name of the output device (null for system default).</summary>
        string? DeviceName { get; }

        /// <summary>Sample rate (Hz) of the output device.</summary>
        int SampleRate { get; }

        /// <summary>Whether the device is open and ready for use.</summary>
        bool IsOpen { get; }

        /// <summary>Open the output device. Pass null for the system default.</summary>
        void Open(string? deviceName = null);

        /// <summary>Close the output device and release all native resources.</summary>
        void Close();

        // --- Listener spatial state (pushed to device) ---

        void SetListenerPosition(Vector3 position);
        void SetListenerVelocity(Vector3 velocity);
        void SetListenerOrientation(Vector3 forward, Vector3 up);
        void SetListenerGain(float gain);

        // --- Source lifecycle ---

        AudioSourceHandle CreateSource();
        void DestroySource(AudioSourceHandle source);

        // --- Buffer lifecycle ---

        AudioBufferHandle CreateBuffer();
        void DestroyBuffer(AudioBufferHandle buffer);

        /// <summary>
        /// Upload raw PCM data to a buffer.
        /// </summary>
        void UploadBufferData(AudioBufferHandle buffer, ReadOnlySpan<byte> pcm, int frequency, int channels, SampleFormat format);

        // --- Playback control (per-source) ---

        void Play(AudioSourceHandle source);
        void Stop(AudioSourceHandle source);
        void Pause(AudioSourceHandle source);
        void SetSourceBuffer(AudioSourceHandle source, AudioBufferHandle buffer);
        void QueueBuffers(AudioSourceHandle source, ReadOnlySpan<AudioBufferHandle> buffers);

        /// <summary>
        /// Unqueue processed buffers from a source. Returns how many were unqueued.
        /// </summary>
        int UnqueueProcessedBuffers(AudioSourceHandle source, Span<AudioBufferHandle> output);

        // --- Source properties ---

        void SetSourcePosition(AudioSourceHandle source, Vector3 position);
        void SetSourceVelocity(AudioSourceHandle source, Vector3 velocity);
        void SetSourceGain(AudioSourceHandle source, float gain);
        void SetSourcePitch(AudioSourceHandle source, float pitch);
        void SetSourceLooping(AudioSourceHandle source, bool loop);
        bool IsSourcePlaying(AudioSourceHandle source);

        // --- Capture (optional) ---

        /// <summary>
        /// Opens a capture device. Returns null if capture is not supported by this transport.
        /// </summary>
        IAudioCaptureDevice? OpenCaptureDevice(string? device, int sampleRate, SampleFormat format, int bufferSize);
    }
}
