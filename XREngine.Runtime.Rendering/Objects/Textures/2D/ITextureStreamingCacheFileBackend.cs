namespace XREngine.Rendering;

/// <summary>Writes staged texture streaming cache payloads on the host.</summary>
public interface ITextureStreamingCacheFileBackend
{
    /// <summary>Creates the cache parent directory when needed.</summary>
    void PrepareDirectory(string cachePath);

    /// <summary>Writes the payload to the cache's temporary file. Consumes the span before returning.</summary>
    void WriteStaged(string cachePath, ReadOnlySpan<byte> payload);

    /// <summary>Publishes the temporary file and applies the source timestamp when possible.</summary>
    void PublishStaged(string cachePath, DateTime sourceLastWriteTimeUtc);
}
