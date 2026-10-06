namespace XREngine.Data;

/// <summary>Opens platform files and provides native mappings.</summary>
public interface IFileMappingBackend
{
    /// <summary>Checks whether a platform file exists with File.Exists behavior.</summary>
    bool FileExists(string path);

    /// <summary>Gets a platform file length with FileInfo.Exists and FileInfo.Length behavior.</summary>
    bool TryGetFileLength(string path, out long length);

    /// <summary>Opens a file. Reports a fallback before copying the source. The caller owns the stream.</summary>
    FileStream OpenFile(string path, bool writable, FileOptions options, Action<string, string> reportFallback);

    /// <summary>Opens a read-write temporary file with random access and delete-on-close behavior. The caller owns the stream.</summary>
    FileStream OpenTemporaryFile(out string path);

    IFileMappingLease Map(FileStream stream, bool writable, long offset, long length);
}
