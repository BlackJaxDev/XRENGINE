using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Audio;

namespace XREngine.Audio.WebAudio;

/// <summary>PCM playback adapter for browser Web Audio. Output stays suspended until a page gesture unlocks it.</summary>
public sealed partial class WebAudioTransport : IAudioSpatialTransport
{
    private int _context;
    private const int MaximumQueuedBuffers = 32;

    public WebAudioTransport() => Open();

    public static void Register()
        => AudioBackendRegistry.RegisterTransport(EAudioTransport.WebAudio, static () => new WebAudioTransport());

    public static Task<bool> UnlockAsync() => WebAudioImports.UnlockAsync();
    public static string State => WebAudioImports.State();
    public static bool IsReady => WebAudioImports.IsReady();
    public static bool HasActivationFailure => WebAudioImports.HasActivationFailure();
    public static string ActivationFailure => WebAudioImports.ActivationFailure();

    /// <summary>Composes page lifetime with the independently owned canvas visibility blocker.</summary>
    public static void SetPageActive(bool active) => WebAudioImports.SetPageActive(active);
    public static void SetSurfaceActive(bool active) => WebAudioImports.SetSurfaceActive(active);

    /// <summary>Coalesces authored pose updates until this synchronous frame batch ends.</summary>
    public static int BeginSpatialUpdates() => WebAudioImports.BeginSpatialUpdates();
    public static void EndSpatialUpdates(int batch) => WebAudioImports.EndSpatialUpdates(batch);

    public string? DeviceName => "Browser default output";
    public int SampleRate => _context == 0 ? 0 : WebAudioImports.SampleRate(_context);
    public bool IsOpen => _context != 0 && WebAudioImports.IsOpen(_context);

    public void Open(string? deviceName = null)
    {
        if (deviceName is not null)
            throw new NotSupportedException("WebAudio.DeviceSelectionUnsupported: browser output device selection is unavailable.");
        if (_context == 0)
            _context = WebAudioImports.Open();
    }

    public void Close()
    {
        if (_context == 0)
            return;
        WebAudioImports.Close(_context);
        _context = 0;
    }

    public void Dispose() => Close();

    public void SetListenerPosition(Vector3 position)
        => WebAudioImports.ListenerPosition(RequireOpen(), position.X, position.Y, position.Z);
    public void SetListenerVelocity(Vector3 velocity)
        => WebAudioImports.ListenerVelocity(RequireOpen(), velocity.X, velocity.Y, velocity.Z);
    public void SetListenerOrientation(Vector3 forward, Vector3 up)
        => WebAudioImports.ListenerOrientation(RequireOpen(), forward.X, forward.Y, forward.Z, up.X, up.Y, up.Z);
    public void SetListenerGain(float gain)
        => WebAudioImports.ListenerGain(RequireOpen(), gain);

    public AudioSourceHandle CreateSource()
    {
        AudioSourceHandle handle = new(checked((uint)WebAudioImports.CreateSource(RequireOpen())));
        return handle;
    }
    public void DestroySource(AudioSourceHandle source)
    {
        WebAudioImports.DestroySource(RequireOpen(), checked((int)source.Id));
    }
    public AudioBufferHandle CreateBuffer()
        => new(checked((uint)WebAudioImports.CreateBuffer(RequireOpen())));
    public void DestroyBuffer(AudioBufferHandle buffer)
        => WebAudioImports.DestroyBuffer(RequireOpen(), checked((int)buffer.Id));
    public void UploadBufferData(AudioBufferHandle buffer, ReadOnlySpan<byte> pcm, int frequency, int channels, SampleFormat format)
    {
        // PCM upload is an asset-boundary operation; JavaScript copies this borrowed view synchronously.
        Span<byte> borrowed = MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(pcm), pcm.Length);
        WebAudioImports.UploadBuffer(RequireOpen(), checked((int)buffer.Id), borrowed, frequency, channels, (int)format);
    }

    public void Play(AudioSourceHandle source)
        => WebAudioImports.Play(RequireOpen(), checked((int)source.Id));
    public void Stop(AudioSourceHandle source)
        => WebAudioImports.Stop(RequireOpen(), checked((int)source.Id));
    public void Pause(AudioSourceHandle source)
        => WebAudioImports.Pause(RequireOpen(), checked((int)source.Id));
    public void Rewind(AudioSourceHandle source)
        => WebAudioImports.Rewind(RequireOpen(), checked((int)source.Id));
    public void SetSourceBuffer(AudioSourceHandle source, AudioBufferHandle buffer)
    {
        WebAudioImports.SetSourceBuffer(RequireOpen(), checked((int)source.Id), checked((int)buffer.Id));
    }
    public void QueueBuffers(AudioSourceHandle source, ReadOnlySpan<AudioBufferHandle> buffers)
    {
        if (buffers.Length > MaximumQueuedBuffers)
            throw new ArgumentOutOfRangeException(nameof(buffers), "Web Audio admits at most 32 queued buffers per source.");
        Span<int> ids = stackalloc int[buffers.Length];
        for (int i = 0; i < buffers.Length; i++)
            ids[i] = checked((int)buffers[i].Id);
        WebAudioImports.QueueBuffers(RequireOpen(), checked((int)source.Id), ids);
    }
    public int UnqueueProcessedBuffers(AudioSourceHandle source, Span<AudioBufferHandle> output)
    {
        Span<int> ids = stackalloc int[MaximumQueuedBuffers];
        int count = WebAudioImports.UnqueueProcessedBuffers(RequireOpen(), checked((int)source.Id), ids,
            Math.Min(output.Length, MaximumQueuedBuffers));
        for (int i = 0; i < count; i++)
            output[i] = new(checked((uint)ids[i]));
        return count;
    }
    public int GetBuffersProcessed(AudioSourceHandle source)
        => WebAudioImports.BuffersProcessed(RequireOpen(), checked((int)source.Id));
    public int GetBuffersQueued(AudioSourceHandle source)
        => WebAudioImports.BuffersQueued(RequireOpen(), checked((int)source.Id));

    public void SetSourcePosition(AudioSourceHandle source, Vector3 position)
        => WebAudioImports.SourcePosition(RequireOpen(), checked((int)source.Id), position.X, position.Y, position.Z);
    public void SetSourceVelocity(AudioSourceHandle source, Vector3 velocity)
        => WebAudioImports.SourceVelocity(RequireOpen(), checked((int)source.Id), velocity.X, velocity.Y, velocity.Z);
    public void SetSourceGain(AudioSourceHandle source, float gain)
        => WebAudioImports.SourceGain(RequireOpen(), checked((int)source.Id), gain);
    public void SetSourcePitch(AudioSourceHandle source, float pitch)
        => WebAudioImports.SourcePitch(RequireOpen(), checked((int)source.Id), pitch);
    public void SetSourceLooping(AudioSourceHandle source, bool loop)
        => WebAudioImports.SourceLooping(RequireOpen(), checked((int)source.Id), loop);
    public bool IsSourcePlaying(AudioSourceHandle source)
        => WebAudioImports.IsSourcePlaying(RequireOpen(), checked((int)source.Id));
    public int GetSampleOffset(AudioSourceHandle source)
        => WebAudioImports.SampleOffset(RequireOpen(), checked((int)source.Id));
    public IAudioCaptureDevice? OpenCaptureDevice(string? device, int sampleRate, SampleFormat format, int bufferSize)
        => null;

    private int RequireOpen()
        => _context != 0 ? _context
            : throw new InvalidOperationException("WebAudio output is not open.");
}
