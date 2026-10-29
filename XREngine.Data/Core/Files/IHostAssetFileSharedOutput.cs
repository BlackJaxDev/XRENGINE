namespace XREngine.Data;

/// <summary>Creates authoring files with an explicit sharing mode.</summary>
public interface IHostAssetFileSharedOutput
{
    /// <summary>Creates or truncates a file and its parent directory. The caller owns the stream.</summary>
    Stream CreateFileWithParentDirectory(string path, FileShare share);
}
