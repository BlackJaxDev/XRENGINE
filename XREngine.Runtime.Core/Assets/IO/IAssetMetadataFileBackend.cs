namespace XREngine.Core.Files;

/// <summary>Provides host file operations for authored asset metadata.</summary>
public interface IAssetMetadataFileBackend
{
    /// <summary>Tests whether a file exists at the path.</summary>
    bool FileExists(string path);
    /// <summary>Tests whether a directory exists at the path.</summary>
    bool DirectoryExists(string path);
    /// <summary>Creates a directory and any missing parent directories.</summary>
    void CreateDirectory(string path);
    /// <summary>Deletes an empty directory.</summary>
    void DeleteDirectory(string path);
    /// <summary>Deletes a file if it exists.</summary>
    void DeleteFile(string path);
    /// <summary>Moves a file and optionally replaces the destination file.</summary>
    void MoveFile(string sourcePath, string destinationPath, bool overwrite);
    /// <summary>Copies a file and optionally replaces the destination file.</summary>
    void CopyFile(string sourcePath, string destinationPath, bool overwrite);
    /// <summary>Reads the full text of a file.</summary>
    string ReadAllText(string path);
    /// <summary>Writes the full text of a file.</summary>
    void WriteAllText(string path, string contents);
    /// <summary>
    /// Reads a top-level ID line with a BOM-aware shared read. Returns Guid.Empty if no ID is valid.
    /// Makes three attempts, with 15 ms and 30 ms waits after the first two I/O failures.
    /// </summary>
    Guid TryExtractAssetGuid(string path);
    /// <summary>Gets the file's last write time in UTC.</summary>
    DateTime GetLastWriteTimeUtc(string path);
}
