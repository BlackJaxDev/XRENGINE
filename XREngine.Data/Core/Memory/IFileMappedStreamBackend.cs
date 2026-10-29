namespace XREngine.Data;

/// <summary>Opens whole-file read-write views without a temporary-copy fallback.</summary>
public interface IFileMappedStreamBackend
{
    /// <summary>Opens a mapped stream. The caller owns the returned lease.</summary>
    IFileMappedStreamLease OpenMappedStream(string path);
}
