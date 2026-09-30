using XREngine.Core.Files;

namespace XREngine.Runtime.Platform.Desktop;

/// <summary>Adapts native desktop watcher events to runtime-neutral asset notifications.</summary>
public sealed class DesktopAssetChangeMonitor : IAssetChangeMonitor
{
    private readonly FileSystemWatcher _watcher = new();
    public DesktopAssetChangeMonitor()
    {
        _watcher.Created += OnCreated;
        _watcher.Changed += OnChanged;
        _watcher.Deleted += OnDeleted;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;
    }
    public string Path { get => _watcher.Path; set => _watcher.Path = value; }
    public string Filter { get => _watcher.Filter; set => _watcher.Filter = value; }
    public bool IncludeSubdirectories { get => _watcher.IncludeSubdirectories; set => _watcher.IncludeSubdirectories = value; }
    public bool EnableRaisingEvents { get => _watcher.EnableRaisingEvents; set => _watcher.EnableRaisingEvents = value; }
    public AssetNotifyFilters NotifyFilter { get => (AssetNotifyFilters)_watcher.NotifyFilter; set => _watcher.NotifyFilter = (NotifyFilters)value; }
    public event Action<object, AssetFileChangeEventArgs>? Created;
    public event Action<object, AssetFileChangeEventArgs>? Changed;
    public event Action<object, AssetFileChangeEventArgs>? Deleted;
    public event Action<object, AssetFileRenameEventArgs>? Renamed;
    public event Action<object, AssetMonitorErrorEventArgs>? Error;
    private void OnCreated(object sender, FileSystemEventArgs args) => Created?.Invoke(this, Convert(args));
    private void OnChanged(object sender, FileSystemEventArgs args) => Changed?.Invoke(this, Convert(args));
    private void OnDeleted(object sender, FileSystemEventArgs args) => Deleted?.Invoke(this, Convert(args));
    private void OnRenamed(object sender, RenamedEventArgs args) => Renamed?.Invoke(this, new(args.FullPath, args.Name, args.OldFullPath, args.OldName));
    private void OnError(object sender, ErrorEventArgs args) => Error?.Invoke(this, new(args.GetException()));
    private static AssetFileChangeEventArgs Convert(FileSystemEventArgs args)
        => new((AssetFileChangeKind)args.ChangeType, args.FullPath, args.Name);
    public void Dispose() => _watcher.Dispose();
}
