namespace XREngine.Data;

/// <summary>Provides file metadata for optional host-side cache validation.</summary>
public interface IRuntimeHostFileMetadataBackend
{
    /// <summary>Gets the write time in UTC ticks and byte length of an existing file. Returns false when metadata is unavailable.</summary>
    bool TryGetFileMetadata(string path, out long lastWriteTimeUtcTicks, out long length);
}
