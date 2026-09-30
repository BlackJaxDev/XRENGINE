namespace XREngine.Core.Files;

/// <summary>Retains monitor configuration while creating a platform watcher only when enabled.</summary>
public sealed class AssetChangeMonitor : IAssetChangeMonitor
{
    private IAssetChangeMonitor? _monitor;
    private string _path = string.Empty;
    private string _filter = "*.*";
    private bool _includeSubdirectories;
    private AssetNotifyFilters _notifyFilter;
    public string Path { get => _path; set { _path = value; if (_monitor is { } monitor) monitor.Path = value; } }
    public string Filter { get => _filter; set { _filter = value; if (_monitor is { } monitor) monitor.Filter = value; } }
    public bool IncludeSubdirectories { get => _includeSubdirectories; set { _includeSubdirectories = value; if (_monitor is { } monitor) monitor.IncludeSubdirectories = value; } }
    public AssetNotifyFilters NotifyFilter { get => _notifyFilter; set { _notifyFilter = value; if (_monitor is { } monitor) monitor.NotifyFilter = value; } }
    public bool EnableRaisingEvents
    {
        get => _monitor?.EnableRaisingEvents ?? false;
        set
        {
            if (value && _monitor is null)
            {
                IAssetFileSystem fileSystem = AssetFileSystemServices.Required;
                if (!fileSystem.SupportsChangeNotifications)
                    throw new NotSupportedException("The installed asset source does not support change notifications.");
                IAssetChangeMonitor monitor = fileSystem.CreateChangeMonitor();
                try
                {
                    monitor.Path = _path;
                    monitor.Filter = _filter;
                    monitor.IncludeSubdirectories = _includeSubdirectories;
                    monitor.NotifyFilter = _notifyFilter;
                    monitor.Created += OnCreated;
                    monitor.Changed += OnChanged;
                    monitor.Deleted += OnDeleted;
                    monitor.Renamed += OnRenamed;
                    monitor.Error += OnError;
                    _monitor = monitor;
                }
                catch { monitor.Dispose(); throw; }
            }
            if (_monitor is { } installed)
                installed.EnableRaisingEvents = value;
        }
    }
    public event Action<object, AssetFileChangeEventArgs>? Created;
    public event Action<object, AssetFileChangeEventArgs>? Changed;
    public event Action<object, AssetFileChangeEventArgs>? Deleted;
    public event Action<object, AssetFileRenameEventArgs>? Renamed;
    public event Action<object, AssetMonitorErrorEventArgs>? Error;
    private void OnCreated(object sender, AssetFileChangeEventArgs args) => Created?.Invoke(this, args);
    private void OnChanged(object sender, AssetFileChangeEventArgs args) => Changed?.Invoke(this, args);
    private void OnDeleted(object sender, AssetFileChangeEventArgs args) => Deleted?.Invoke(this, args);
    private void OnRenamed(object sender, AssetFileRenameEventArgs args) => Renamed?.Invoke(this, args);
    private void OnError(object sender, AssetMonitorErrorEventArgs args) => Error?.Invoke(this, args);
    public void Dispose() => Interlocked.Exchange(ref _monitor, null)?.Dispose();
}
