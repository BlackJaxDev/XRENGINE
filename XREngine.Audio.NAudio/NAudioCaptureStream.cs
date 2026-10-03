using NAudio.Wave;

namespace XREngine.Audio;

/// <summary>NAudio microphone capture stream for desktop hosts.</summary>
public sealed class NAudioCaptureStream : IAudioCaptureStream
{
    private readonly WaveInEvent _input;

    public event Action<byte[], int>? DataAvailable;

    public NAudioCaptureStream(int deviceIndex, int sampleRate, int bitsPerSample, int bufferMilliseconds)
    {
        _input = new WaveInEvent
        {
            DeviceNumber = deviceIndex,
            WaveFormat = new WaveFormat(sampleRate, bitsPerSample, channels: 1),
            BufferMilliseconds = bufferMilliseconds,
        };
        _input.DataAvailable += OnDataAvailable;
    }

    public static string[] GetDeviceNames()
    {
        string[] names = new string[WaveInEvent.DeviceCount];
        for (int i = 0; i < names.Length; i++)
            names[i] = WaveInEvent.GetCapabilities(i).ProductName;
        return names;
    }

    public static string[] GetAsioDriverNames() => AsioOut.GetDriverNames();

    public void Start() => _input.StartRecording();
    public void Stop() => _input.StopRecording();

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
        => DataAvailable?.Invoke(args.Buffer, args.BytesRecorded);

    public void Dispose()
    {
        _input.DataAvailable -= OnDataAvailable;
        _input.Dispose();
    }
}
