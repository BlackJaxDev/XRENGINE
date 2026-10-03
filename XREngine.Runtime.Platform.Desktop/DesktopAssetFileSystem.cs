using XREngine.Core.Files;

namespace XREngine.Runtime.Platform.Desktop;

/// <summary>Desktop host file discovery through the operating system's ordinary filesystem permissions.</summary>
public sealed class DesktopAssetFileSystem : IAssetFileSystem
{
    public bool SupportsChangeNotifications => true;
    public IEnumerable<string> EnumerateFiles(string path, string pattern, SearchOption searchOption)
        => Directory.EnumerateFiles(path, pattern, searchOption);
    public IEnumerable<string> EnumerateDirectories(string path, string pattern, SearchOption searchOption)
        => Directory.EnumerateDirectories(path, pattern, searchOption);
    public IEnumerable<string> EnumerateFileSystemEntries(string path)
        => Directory.EnumerateFileSystemEntries(path);
    public IAssetChangeMonitor CreateChangeMonitor() => new DesktopAssetChangeMonitor();
}
