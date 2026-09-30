namespace XREngine.Core.Files;

/// <summary>Provides host file discovery and optional native change notifications.</summary>
public interface IAssetFileSystem : XREngine.Data.IRuntimeFileDiscovery
{
    bool SupportsChangeNotifications { get; }
    IAssetChangeMonitor CreateChangeMonitor();
}
