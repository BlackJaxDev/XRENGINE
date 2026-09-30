namespace XREngine.Core.Files;

/// <summary>Owns optional file-change notifications independently of the runtime asset manager.</summary>
public interface IAssetChangeMonitor : IDisposable
{
    string Path { get; set; }
    string Filter { get; set; }
    bool IncludeSubdirectories { get; set; }
    bool EnableRaisingEvents { get; set; }
    AssetNotifyFilters NotifyFilter { get; set; }
    event Action<object, AssetFileChangeEventArgs>? Created;
    event Action<object, AssetFileChangeEventArgs>? Changed;
    event Action<object, AssetFileChangeEventArgs>? Deleted;
    event Action<object, AssetFileRenameEventArgs>? Renamed;
    event Action<object, AssetMonitorErrorEventArgs>? Error;
}
