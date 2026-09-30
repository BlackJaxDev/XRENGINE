namespace XREngine.Core.Files;

/// <summary>Stores the host's explicitly installed asset file-discovery capability.</summary>
public static class AssetFileSystemServices
{
    private static IAssetFileSystem? _current;
    public static IAssetFileSystem? Current
    {
        get => Volatile.Read(ref _current);
        set
        {
            Volatile.Write(ref _current, value);
            XREngine.Data.RuntimeFileDiscoveryServices.Current = value;
        }
    }
    public static bool SupportsChangeNotifications => Current?.SupportsChangeNotifications ?? false;
    public static IAssetFileSystem Required => Current ??
        throw new NotSupportedException("Asset file discovery is not installed. Install an asset file-system backend in the host.");
}
