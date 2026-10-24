namespace XREngine.Core.Files;

/// <summary>Binds one change monitor to the file-system installation that created it.</summary>
public sealed class AssetChangeMonitorInstallation(
    IAssetChangeMonitor monitor, IAssetFileSystem fileSystem, long generation)
{
    public IAssetChangeMonitor Monitor { get; } = monitor;
    public IAssetFileSystem FileSystem { get; } = fileSystem;
    public long Generation { get; } = generation;
}
