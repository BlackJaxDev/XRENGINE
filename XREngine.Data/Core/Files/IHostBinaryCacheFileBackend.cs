namespace XREngine.Data;

/// <summary>Reads and atomically replaces host binary cache files.</summary>
public interface IHostBinaryCacheFileBackend : IRuntimeHostFileReadBackend
{
    /// <summary>Writes a unique sibling temporary file and replaces the cache path.</summary>
    void WriteAllBytesAtomic(string path, byte[] bytes);
}
