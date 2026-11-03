namespace XREngine.Core.Files;

/// <summary>Stores the host's explicitly installed asset file-discovery capability.</summary>
/// <remarks>
/// The file system and its generation are published together as one immutable
/// box, so captures and currency checks read them without a lock.
/// </remarks>
public static class AssetFileSystemServices
{
    private static readonly object Gate = new();
    private static System.Runtime.CompilerServices.StrongBox<(IAssetFileSystem? FileSystem, long Generation)> _installation = new((null, 0));
    public static IAssetFileSystem? Current
    {
        get => Volatile.Read(ref _installation).Value.FileSystem;
        set
        {
            lock (Gate)
            {
                long generation = unchecked(_installation.Value.Generation + 1);
                Volatile.Write(ref _installation, new((value, generation)));
                XREngine.Data.RuntimeFileDiscoveryServices.Current = value;
            }
        }
    }
    public static bool SupportsChangeNotifications => Current?.SupportsChangeNotifications ?? false;
    public static IAssetFileSystem Required => Current ??
        throw new NotSupportedException("Asset file discovery is not installed. Install an asset file-system backend in the host.");

    /// <summary>Captures one host file provider after host-file admission.</summary>
    public static IAssetMetadataFileBackend CaptureMetadataFileBackend(string operation)
    {
        XREngine.Data.RuntimeAssetReadServices.EnsureHostFileAccess(operation);
        IAssetFileSystem fileSystem = Required;
        if (fileSystem is not IAssetMetadataFileBackend files)
            throw new NotSupportedException("AssetSource.MetadataFileUnavailable: the installed asset file system does not provide metadata file operations.");

        return files;
    }

    /// <summary>Captures one asset discovery installation.</summary>
    public static bool TryCapture(out IAssetFileSystem? fileSystem, out long generation)
    {
        (fileSystem, generation) = Volatile.Read(ref _installation).Value;
        return fileSystem is not null;
    }

    /// <summary>Checks if the captured asset discovery installation is current.</summary>
    public static bool IsCurrent(IAssetFileSystem? fileSystem, long generation)
    {
        (IAssetFileSystem? current, long currentGeneration) = Volatile.Read(ref _installation).Value;
        return ReferenceEquals(current, fileSystem) && currentGeneration == generation;
    }
}
