namespace XREngine.Audio
{
    /// <summary>
    /// Abstraction for audio capture (microphone input).
    /// Returned by <see cref="IAudioTransport.OpenCaptureDevice"/>.
    /// </summary>
    public interface IAudioCaptureDevice : IDisposable
    {
        /// <summary>Name of the capture device.</summary>
        string? DeviceName { get; }

        /// <summary>Sample rate (Hz) of the capture device.</summary>
        int SampleRate { get; }

        /// <summary>Sample format of the captured data.</summary>
        SampleFormat Format { get; }

        /// <summary>Whether the device is currently capturing.</summary>
        bool IsCapturing { get; }

        /// <summary>Start capturing audio samples.</summary>
        void Start();

        /// <summary>Stop capturing audio samples.</summary>
        void Stop();

        /// <summary>Number of captured samples available for reading.</summary>
        int AvailableSamples { get; }

        /// <summary>
        /// Read captured samples into the provided buffer.
        /// Returns the number of samples actually read.
        /// </summary>
        int ReadSamples(Span<byte> output, int sampleCount);
    }
}
