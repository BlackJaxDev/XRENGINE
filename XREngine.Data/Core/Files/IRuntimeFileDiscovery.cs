namespace XREngine.Data;

/// <summary>Provides file discovery through the host's storage capability.</summary>
public interface IRuntimeFileDiscovery
{
    IEnumerable<string> EnumerateFiles(string path, string pattern, SearchOption searchOption);
    IEnumerable<string> EnumerateDirectories(string path, string pattern, SearchOption searchOption);
    IEnumerable<string> EnumerateFileSystemEntries(string path);
}
