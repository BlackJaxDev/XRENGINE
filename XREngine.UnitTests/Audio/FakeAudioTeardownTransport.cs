using System.Numerics;
using XREngine.Audio;

namespace XREngine.UnitTests.Audio;

internal sealed class FakeAudioTeardownTransport : IAudioTransport
{
    private uint _nextSource = 1;
    public string? DeviceName => null;
    public int SampleRate => 48000;
    public bool IsOpen { get; private set; } = true;
    public int CreatedSources { get; private set; }
    public int DestroyedSources { get; private set; }
    public Action<AudioSourceHandle>? OnCreateSource { get; set; }
    public Action<AudioSourceHandle>? OnDestroySource { get; set; }

    public void Open(string? deviceName = null) => IsOpen = true;
    public void Close() => IsOpen = false;
    public void Dispose() => Close();
    public void SetListenerPosition(Vector3 position) { }
    public void SetListenerVelocity(Vector3 velocity) { }
    public void SetListenerOrientation(Vector3 forward, Vector3 up) { }
    public void SetListenerGain(float gain) { }
    public AudioSourceHandle CreateSource()
    {
        CreatedSources++;
        AudioSourceHandle source = new(_nextSource++);
        OnCreateSource?.Invoke(source);
        return source;
    }
    public void DestroySource(AudioSourceHandle source)
    {
        DestroyedSources++;
        OnDestroySource?.Invoke(source);
    }
    public AudioBufferHandle CreateBuffer() => new(1);
    public void DestroyBuffer(AudioBufferHandle buffer) { }
    public void UploadBufferData(AudioBufferHandle buffer, ReadOnlySpan<byte> pcm,
        int frequency, int channels, SampleFormat format) { }
    public void Play(AudioSourceHandle source) { }
    public void Stop(AudioSourceHandle source) { }
    public void Pause(AudioSourceHandle source) { }
    public void Rewind(AudioSourceHandle source) { }
    public void SetSourceBuffer(AudioSourceHandle source, AudioBufferHandle buffer) { }
    public void QueueBuffers(AudioSourceHandle source, ReadOnlySpan<AudioBufferHandle> buffers) { }
    public int UnqueueProcessedBuffers(AudioSourceHandle source, Span<AudioBufferHandle> output) => 0;
    public int GetBuffersProcessed(AudioSourceHandle source) => 0;
    public int GetBuffersQueued(AudioSourceHandle source) => 0;
    public void SetSourcePosition(AudioSourceHandle source, Vector3 position) { }
    public void SetSourceVelocity(AudioSourceHandle source, Vector3 velocity) { }
    public void SetSourceGain(AudioSourceHandle source, float gain) { }
    public void SetSourcePitch(AudioSourceHandle source, float pitch) { }
    public void SetSourceLooping(AudioSourceHandle source, bool loop) { }
    public bool IsSourcePlaying(AudioSourceHandle source) => false;
    public int GetSampleOffset(AudioSourceHandle source) => 0;
    public IAudioCaptureDevice? OpenCaptureDevice(string? device, int sampleRate,
        SampleFormat format, int bufferSize) => null;
}
