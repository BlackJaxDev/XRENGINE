using XREngine.Data;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Adds one more file path to the files that the inner discovery finds in one directory.
/// Tests use it to put an entry name into a <c>.pak</c> that a plain directory cannot give,
/// for example <c>../outside.asset</c>.
/// </summary>
internal sealed class AppendedFileDiscovery(IRuntimeFileDiscovery inner, string directory, string appendedFilePath) : IRuntimeFileDiscovery
{
    private readonly string _directory = Path.GetFullPath(directory);

    public IEnumerable<string> EnumerateFiles(string path, string pattern, SearchOption searchOption)
    {
        IEnumerable<string> files = inner.EnumerateFiles(path, pattern, searchOption);
        return string.Equals(Path.GetFullPath(path), _directory, StringComparison.OrdinalIgnoreCase)
            ? files.Append(appendedFilePath)
            : files;
    }

    public IEnumerable<string> EnumerateDirectories(string path, string pattern, SearchOption searchOption)
        => inner.EnumerateDirectories(path, pattern, searchOption);

    public IEnumerable<string> EnumerateFileSystemEntries(string path)
        => inner.EnumerateFileSystemEntries(path);
}
