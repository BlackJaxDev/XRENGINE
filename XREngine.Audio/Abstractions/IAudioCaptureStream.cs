namespace XREngine.Audio;

/// <summary>Event-driven microphone capture device owned by an audio backend.</summary>
public interface IAudioCaptureStream : IDisposable
{
    event Action<byte[], int>? DataAvailable;
    void Start();
    void Stop();
}

/// <summary>Explicit desktop microphone capture registration.</summary>
public static class AudioCaptureRegistry
{
    private static Func<int, int, int, int, IAudioCaptureStream>? _factory;
    private static Func<string[]>? _deviceNames;
    private static Func<string[]>? _asioDriverNames;

    public static void Register(
        Func<int, int, int, int, IAudioCaptureStream> factory,
        Func<string[]> deviceNames,
        Func<string[]> asioDriverNames)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _deviceNames = deviceNames ?? throw new ArgumentNullException(nameof(deviceNames));
        _asioDriverNames = asioDriverNames ?? throw new ArgumentNullException(nameof(asioDriverNames));
    }

    public static IAudioCaptureStream Create(int deviceIndex, int sampleRate, int bitsPerSample, int bufferMilliseconds)
        => (_factory ?? throw new InvalidOperationException("No microphone capture backend is registered."))(
            deviceIndex, sampleRate, bitsPerSample, bufferMilliseconds);

    public static string[] GetDeviceNames()
        => (_deviceNames ?? throw new InvalidOperationException("No microphone capture backend is registered."))();

    public static string[] GetAsioDriverNames()
        => (_asioDriverNames ?? throw new InvalidOperationException("No microphone capture backend is registered."))();
}
