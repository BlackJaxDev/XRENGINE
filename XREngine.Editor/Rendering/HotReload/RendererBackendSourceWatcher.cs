using XREngine.Rendering;

namespace XREngine.Editor.HotReload;

/// <summary>
/// Debounces backend source changes without observing build, shadow-copy, or asset output.
/// </summary>
internal sealed class RendererBackendSourceWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounceTimer;
    private readonly Func<RendererBackendId, Task> _reload;
    private readonly RendererBackendId _backendId;
    private readonly int _debounceMilliseconds;
    private int _disposed;

    public RendererBackendSourceWatcher(
        string projectDirectory,
        RendererBackendId backendId,
        int debounceMilliseconds,
        Func<RendererBackendId, Task> reload)
    {
        _backendId = backendId;
        _debounceMilliseconds = Math.Clamp(debounceMilliseconds, 100, 10000);
        _reload = reload;
        _debounceTimer = new(
            static state => ((RendererBackendSourceWatcher)state!).OnDebounceElapsed(),
            this,
            Timeout.Infinite,
            Timeout.Infinite);
        _watcher = new(projectDirectory, "*.cs")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName |
                NotifyFilters.LastWrite |
                NotifyFilters.CreationTime |
                NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnRenamed;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnChanged;
        _watcher.Created -= OnChanged;
        _watcher.Deleted -= OnChanged;
        _watcher.Renamed -= OnRenamed;
        _watcher.Dispose();
        _debounceTimer.Dispose();
    }

    private void OnChanged(object sender, FileSystemEventArgs args)
    {
        if (IsGeneratedPath(args.FullPath))
            return;

        _debounceTimer.Change(_debounceMilliseconds, Timeout.Infinite);
    }

    private void OnRenamed(object sender, RenamedEventArgs args)
        => OnChanged(sender, args);

    private void OnDebounceElapsed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        _ = ObserveReloadAsync();
    }

    private async Task ObserveReloadAsync()
    {
        try
        {
            await _reload(_backendId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex, "Automatic renderer backend reload failed.");
        }
    }

    private static bool IsGeneratedPath(string path)
        => path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
           path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
           path.Contains($"{Path.DirectorySeparatorChar}Build{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
}
