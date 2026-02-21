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
}
