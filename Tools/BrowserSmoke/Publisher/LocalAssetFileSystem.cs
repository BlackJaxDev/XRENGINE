using XREngine.Core.Files;

/// <summary>Supplies the Editor publisher with real, immutable local authored input files.</summary>
internal sealed class LocalAssetFileSystem : IAssetFileSystem
{
    public bool SupportsChangeNotifications => false;
    public IEnumerable<string> EnumerateFiles(string path, string pattern, SearchOption option)
        => Directory.EnumerateFiles(path, pattern, option);
    public IEnumerable<string> EnumerateDirectories(string path, string pattern, SearchOption option)
        => Directory.EnumerateDirectories(path, pattern, option);
    public IEnumerable<string> EnumerateFileSystemEntries(string path)
        => Directory.EnumerateFileSystemEntries(path);
    public IAssetChangeMonitor CreateChangeMonitor()
        => throw new NotSupportedException("The publisher qualifier does not monitor source changes.");
}
