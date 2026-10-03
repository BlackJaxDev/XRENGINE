using XREngine.Rendering.VideoStreaming.Interfaces;

namespace XREngine.Rendering.VideoStreaming;

/// <summary>Provides the native media leaf's optional external URL resolution capability.</summary>
public static class YouTubeStreamUrlResolverRegistry
{
    private static IYouTubeStreamUrlResolver? _current;

    public static IYouTubeStreamUrlResolver? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }

    public static IYouTubeStreamUrlResolver Require()
        => Current ?? throw new NotSupportedException(
            "YouTube source resolution is unavailable. Install a media URL resolver in the application host.");
}
