using XREngine.Data;

namespace XREngine.Core.Files.Caching;

/// <summary>Receives the file provider captured by the cache operation.</summary>
public interface IHostFileCacheCodec
{
    /// <summary>Reads a cache payload through the captured provider.</summary>
    CacheReadResult Read(string cachePath, string originalPath, DateTime sourceTimestampUtc, IHostBinaryCacheFileBackend files);

    /// <summary>Writes a cache payload through the captured provider.</summary>
    CacheWriteResult Write(string cachePath, XRAsset cacheAsset, XRAsset originalAsset, IHostBinaryCacheFileBackend files);
}
