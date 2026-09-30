namespace XREngine.Rendering.VideoStreaming.Interfaces;

/// <summary>Resolves a YouTube source through an explicitly installed external media tool.</summary>
public interface IYouTubeStreamUrlResolver
{
    Task<string> ResolveAsync(string source, CancellationToken cancellationToken);
}
