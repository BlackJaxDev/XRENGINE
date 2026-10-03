using XREngine.Rendering.VideoStreaming.Interfaces;

namespace XREngine.Rendering.VideoStreaming;

/// <summary>Entry point for streamed media playback in the rendering runtime.</summary>
public static class HlsReferenceRuntime
{
    private static IHlsReferenceRuntime? _backend;

    public static IHlsReferenceRuntime? Backend
    {
        get => Volatile.Read(ref _backend);
        set => Volatile.Write(ref _backend, value);
    }

    public static bool EnsureStarted()
        => (Backend ?? throw MissingBackend()).EnsureStarted();

    public static IMediaStreamSession CreateSession()
        => (Backend ?? throw MissingBackend()).CreateSession();

    private static InvalidOperationException MissingBackend()
        => new("No media stream decoder is registered. Register the FFmpeg media backend in the application composition root.");
}
