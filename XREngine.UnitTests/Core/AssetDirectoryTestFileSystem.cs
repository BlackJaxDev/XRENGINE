using XREngine.Core.Files;

namespace XREngine.UnitTests.Core;

/// <summary>Records directory operations without accessing host files.</summary>
internal sealed class AssetDirectoryTestFileSystem : IAssetFileSystem, IAssetMetadataFileBackend
{
    public HashSet<string> Directories { get; } = new(StringComparer.Ordinal);
    public List<string> Probes { get; } = [];
    public List<string> Created { get; } = [];
    public Action? OnProbe { get; set; }
    public bool SupportsChangeNotifications => false;

    public bool DirectoryExists(string path)
    {
        Probes.Add(path);
        OnProbe?.Invoke();
        return Directories.Contains(path);
    }

    public void CreateDirectory(string path)
    {
        Created.Add(path);
        Directories.Add(path);
    }

    public IAssetChangeMonitor CreateChangeMonitor() => throw new NotSupportedException();
    public IEnumerable<string> EnumerateFiles(string path, string pattern, SearchOption option) => throw new NotSupportedException();
    public IEnumerable<string> EnumerateDirectories(string path, string pattern, SearchOption option) => throw new NotSupportedException();
    public IEnumerable<string> EnumerateFileSystemEntries(string path) => throw new NotSupportedException();
    public bool FileExists(string path) => throw new NotSupportedException();
    public void DeleteDirectory(string path) => throw new NotSupportedException();
    public void DeleteFile(string path) => throw new NotSupportedException();
    public void MoveFile(string source, string destination, bool overwrite) => throw new NotSupportedException();
    public void CopyFile(string source, string destination, bool overwrite) => throw new NotSupportedException();
    public string ReadAllText(string path) => throw new NotSupportedException();
    public void WriteAllText(string path, string contents) => throw new NotSupportedException();
    public Guid TryExtractAssetGuid(string path) => throw new NotSupportedException();
    public DateTime GetLastWriteTimeUtc(string path) => throw new NotSupportedException();
}
