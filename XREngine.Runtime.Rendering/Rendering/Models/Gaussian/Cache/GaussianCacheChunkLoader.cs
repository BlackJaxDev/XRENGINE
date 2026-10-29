using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace XREngine.Rendering.Models.Gaussian.Cache;

/// <summary>
/// Loads and accepts the chunks of a cache in order. More than one loader can run on one cache at the same time: each
/// loader gets the next chunk under the decode lock of the cache, and a chunk that another loader accepted first counts
/// as accepted, so no chunk decodes two times.
/// </summary>
public static class GaussianCacheChunkLoader
{
    /// <summary>
    /// Returns the path of a chunk asset: the chunk path in the folder of the root asset.
    /// </summary>
    /// <param name="rootAssetPath">The path of the root asset.</param>
    /// <param name="chunkAssetPath">The chunk path of the chunk table, relative to the folder of the root asset.</param>
    /// <exception cref="GaussianCacheFormatException">
    /// The chunk path is not a relative path in the folder of the root asset (see
    /// <see cref="GaussianCache.IsValidChunkAssetPath"/>; <see cref="GaussianCacheFormatException.InvalidChunkPathCode"/>).
    /// </exception>
    public static string ResolveChunkPath(string rootAssetPath, string chunkAssetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootAssetPath);
        if (!GaussianCache.IsValidChunkAssetPath(chunkAssetPath))
            throw new GaussianCacheFormatException(
                GaussianCacheFormatException.InvalidChunkPathCode,
                $"The chunk path '{chunkAssetPath}' is not a relative path in the folder of the root asset.");

        string? folder = Path.GetDirectoryName(rootAssetPath);
        return string.IsNullOrEmpty(folder) ? chunkAssetPath : Path.Combine(folder, chunkAssetPath);
    }

    /// <summary>
    /// Loads and accepts the chunks of a cache that are not accepted yet, in order, until the cache is complete or
    /// <paramref name="keepLoading"/> returns false.
    /// </summary>
    /// <param name="cache">The cache. Its root asset is loaded.</param>
    /// <param name="rootAssetPath">The path of the root asset. Chunk paths are relative to its folder.</param>
    /// <param name="loadChunk">Loads the chunk asset at a path. It returns null when the asset cannot be loaded.</param>
    /// <param name="keepLoading">Returns false when the caller does not need the cache any more.</param>
    /// <param name="cancellationToken">Stops the load.</param>
    /// <exception cref="FileNotFoundException">A chunk could not be loaded.</exception>
    /// <exception cref="GaussianCacheFormatException">The cache, a chunk path, or a chunk is not valid.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static async Task LoadChunksAsync(
        GaussianCache cache,
        string rootAssetPath,
        Func<string, CancellationToken, Task<GaussianCacheChunk?>> loadChunk,
        Func<bool> keepLoading,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootAssetPath);
        ArgumentNullException.ThrowIfNull(loadChunk);
        ArgumentNullException.ThrowIfNull(keepLoading);

        while (keepLoading())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!cache.TryGetNextChunk(out _, out string relativePath))
                return;

            string chunkPath = ResolveChunkPath(rootAssetPath, relativePath);
            GaussianCacheChunk chunk = await loadChunk(chunkPath, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException($"The gaussian cache chunk '{chunkPath}' could not be loaded.", chunkPath);
            cancellationToken.ThrowIfCancellationRequested();
            if (!keepLoading())
                return;

            // The cache raises a NextChunkIndex change when it decodes the chunk. A chunk that another loader accepted
            // first does not decode again.
            cache.AcceptChunk(chunk);
        }
    }
}
