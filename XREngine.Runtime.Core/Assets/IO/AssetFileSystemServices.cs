namespace XREngine.Core.Files;

/// <summary>Stores the host's explicitly installed asset file-discovery capability.</summary>
public static class AssetFileSystemServices
{
    private static readonly object Gate = new();
    private static IAssetFileSystem? _current;
    private static long _generation;
    public static IAssetFileSystem? Current
    {
        get => Volatile.Read(ref _current);
        set
        {
            lock (Gate)
            {
                Volatile.Write(ref _current, value);
                _generation = unchecked(_generation + 1);
                XREngine.Data.RuntimeFileDiscoveryServices.Current = value;
            }
        }
    }
    public static bool SupportsChangeNotifications => Current?.SupportsChangeNotifications ?? false;
    public static IAssetFileSystem Required => Current ??
        throw new NotSupportedException("Asset file discovery is not installed. Install an asset file-system backend in the host.");

    /// <summary>Captures one asset discovery installation.</summary>
    public static bool TryCapture(out IAssetFileSystem? fileSystem, out long generation)
    {
        lock (Gate)
        {
            fileSystem = _current;
            generation = _generation;
            return fileSystem is not null;
        }
    }

    /// <summary>Checks if the captured asset discovery installation is current.</summary>
    public static bool IsCurrent(IAssetFileSystem? fileSystem, long generation)
    {
        lock (Gate)
            return ReferenceEquals(_current, fileSystem) && _generation == generation;
    }
}
