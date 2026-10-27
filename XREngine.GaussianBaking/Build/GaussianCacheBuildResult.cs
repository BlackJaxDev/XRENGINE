using XREngine.Rendering.Models.Gaussian.Cache;

namespace XREngine.Rendering.Models.Gaussian.Baking.Build;

/// <summary>The cache that <see cref="GaussianCacheBuilder"/> makes: the root asset and the chunks after it.</summary>
public sealed class GaussianCacheBuildResult(GaussianCache root, GaussianCacheChunk[] chunks, string contentHash, long totalBytes, int chunk0Bytes)
{
    /// <summary>The root asset. It holds the header and the data of chunk 0.</summary>
    public GaussianCache Root { get; } = root;

    /// <summary>Chunks 1 to n, in order. Item <c>i</c> has the index <c>i + 1</c>.</summary>
    public GaussianCacheChunk[] Chunks { get; } = chunks;

    /// <summary>
    /// The SHA-256 hash, as lowercase hex, of the header bytes, then the data of chunk 0, then the data of each chunk in order.
    /// </summary>
    public string ContentHash { get; } = contentHash;

    /// <summary>The header bytes plus the data bytes of all chunks.</summary>
    public long TotalBytes { get; } = totalBytes;

    /// <summary>The header bytes plus the data bytes of chunk 0.</summary>
    public int Chunk0Bytes { get; } = chunk0Bytes;
}
