namespace XREngine.Core.Files;

/// <summary>Retains monitor configuration while creating a platform watcher only when enabled.</summary>
public sealed class AssetChangeMonitor : IAssetChangeMonitor
{
    private readonly object _gate = new();
    private AssetChangeMonitorInstallation? _installation;
    private bool _enabledPreference;
    private bool _disposed;
    private long _installationRevision;
    private string _path = string.Empty;
    private string _filter = "*.*";
    private bool _includeSubdirectories;
    private AssetNotifyFilters _notifyFilter;
    public string Path
    {
        get => _path;
        set
        {
            lock (_gate)
            {
                if (_path == value)
                    return;
                _path = value;
                Rebind();
            }
        }
    }
    public string Filter { get => _filter; set { _filter = value; if (_installation is { } installation) installation.Monitor.Filter = value; } }
    public bool IncludeSubdirectories { get => _includeSubdirectories; set { _includeSubdirectories = value; if (_installation is { } installation) installation.Monitor.IncludeSubdirectories = value; } }
    public AssetNotifyFilters NotifyFilter { get => _notifyFilter; set { _notifyFilter = value; if (_installation is { } installation) installation.Monitor.NotifyFilter = value; } }
    public bool EnableRaisingEvents
    {
        get => Volatile.Read(ref _installation)?.Monitor.EnableRaisingEvents ?? false;
        set
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    if (value)
                        throw new ObjectDisposedException(nameof(AssetChangeMonitor));
                    return;
                }
                _enabledPreference = value;
                long revision = unchecked(++_installationRevision);
                if (value && _installation is null)
                {
                    if (!AssetFileSystemServices.TryCapture(out IAssetFileSystem? fileSystem, out long generation) ||
                        fileSystem is null)
                        throw new NotSupportedException("Asset file discovery is not installed.");
                    if (!fileSystem.SupportsChangeNotifications)
                        throw new NotSupportedException("The installed asset source does not support change notifications.");
                    IAssetChangeMonitor monitor = fileSystem.CreateChangeMonitor();
                    try
                    {
                        monitor.Path = _path;
                        monitor.Filter = _filter;
                        monitor.IncludeSubdirectories = _includeSubdirectories;
                        monitor.NotifyFilter = _notifyFilter;
                        AssetChangeMonitorInstallation installation = new(monitor, fileSystem, generation);
                        Bind(installation);
                        if (_disposed || !_enabledPreference || revision != _installationRevision ||
                            !AssetFileSystemServices.IsCurrent(fileSystem, generation))
                        {
                            if (!_disposed && _enabledPreference && revision == _installationRevision)
                                global::XREngine.Debug.LogWarning("Asset change monitor installation changed while it was starting. Rebind after the file-system change.");
                            QueueDispose(monitor);
                            return;
                        }
                        Volatile.Write(ref _installation, installation);
                    }
                    catch { QueueDispose(monitor); throw; }
                }
                if (_installation is { } installed)
                    installed.Monitor.EnableRaisingEvents = value;
            }
        }
    }
    public event Action<object, AssetFileChangeEventArgs>? Created;
    public event Action<object, AssetFileChangeEventArgs>? Changed;
    public event Action<object, AssetFileChangeEventArgs>? Deleted;
    public event Action<object, AssetFileRenameEventArgs>? Renamed;
    public event Action<object, AssetMonitorErrorEventArgs>? Error;
    /// <summary>Gets the current monitor installation identity.</summary>
    public AssetChangeMonitorInstallation? CurrentInstallation => Volatile.Read(ref _installation);

    /// <summary>Replaces the underlying monitor so old queued events retain their old identity.</summary>
    public void Rebind()
    {
        AssetChangeMonitorInstallation? old;
        lock (_gate)
        {
            if (_disposed)
                return;
            _installationRevision = unchecked(_installationRevision + 1);
            old = Interlocked.Exchange(ref _installation, null);
            try
            {
                if (_enabledPreference)
                    EnableRaisingEvents = true;
            }
            finally
            {
                // Disposal can wait for a callback. The caller may hold the asset metadata lock.
                if (old is not null)
                    QueueDispose(old.Monitor);
            }
        }
    }

    private void Bind(AssetChangeMonitorInstallation installation)
    {
        IAssetChangeMonitor monitor = installation.Monitor;
        monitor.Created += (_, args) => Created?.Invoke(installation, args);
        monitor.Changed += (_, args) => Changed?.Invoke(installation, args);
        monitor.Deleted += (_, args) => Deleted?.Invoke(installation, args);
        monitor.Renamed += (_, args) => Renamed?.Invoke(installation, args);
        monitor.Error += (_, args) => Error?.Invoke(installation, args);
    }

    private static void QueueDispose(IAssetChangeMonitor monitor)
    {
        ThreadPool.QueueUserWorkItem(static state =>
        {
            try { ((IAssetChangeMonitor)state!).Dispose(); }
            catch (Exception exception)
            {
                global::XREngine.Debug.LogWarning($"Asset change monitor disposal failed: {exception.Message}");
            }
        }, monitor);
    }

    public void Dispose()
    {
        AssetChangeMonitorInstallation? installation;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _enabledPreference = false;
            _installationRevision = unchecked(_installationRevision + 1);
            installation = Interlocked.Exchange(ref _installation, null);
        }
        installation?.Monitor.Dispose();
    }
}
