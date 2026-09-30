using XREngine.Rendering.VideoStreaming;

namespace XREngine.Runtime.Media.FFmpeg;

/// <summary>Installs the native FFmpeg implementation of streamed media playback.</summary>
public static class FfmpegMediaBackend
{
    public static void Register()
    {
        HlsReferenceRuntime.Backend = new FfmpegHlsReferenceRuntime();
        YouTubeStreamUrlResolverRegistry.Current = new YtDlpStreamUrlResolver();
    }
}
