namespace XREngine.Data;

/// <summary>Opens platform files and provides native mappings.</summary>
public interface IFileMappingBackend
{
    /// <summary>Opens a file. Reports a fallback before copying the source. The caller owns the stream.</summary>
    FileStream OpenFile(string path, bool writable, FileOptions options, Action<string, string> reportFallback);

    /// <summary>Opens a read-write temporary file with random access and delete-on-close behavior. The caller owns the stream.</summary>
    FileStream OpenTemporaryFile(out string path);

    IFileMappingLease Map(FileStream stream, bool writable, long offset, long length);
}
