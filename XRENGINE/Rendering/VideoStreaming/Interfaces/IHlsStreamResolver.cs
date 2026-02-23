using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace XREngine.Rendering.VideoStreaming.Interfaces;

public interface IHlsStreamResolver
{
    Task<ResolvedStream> ResolveAsync(string source, CancellationToken cancellationToken);
}

public sealed class ResolvedStream
{
    public required string Url { get; init; }
    public StreamOpenOptions? OpenOptions { get; init; }
    public int RetryCount { get; init; }

    /// <summary>
    /// All quality variants parsed from the master playlist, ordered highest
    /// bandwidth first.  Empty when the source is already a media playlist.
    /// </summary>
    public IReadOnlyList<StreamVariantInfo> AvailableQualities { get; init; } = [];
}
