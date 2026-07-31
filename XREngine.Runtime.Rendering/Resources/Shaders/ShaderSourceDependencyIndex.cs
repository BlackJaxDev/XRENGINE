using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace XREngine.Rendering;

/// <summary>
/// Maintains the reverse dependency graph from normalized shader source files to loaded shaders.
/// Entries retain shaders weakly so the index cannot extend an asset's lifetime.
/// </summary>
internal static class ShaderSourceDependencyIndex
{
    private sealed class ShaderDependencies
    {
        public string[] Paths { get; set; } = [];
    }

    private sealed class PendingChange(ShaderSourceFileChange change, CancellationTokenSource cancellation)
    {
        public ShaderSourceFileChange Change { get; } = change;
        public CancellationTokenSource Cancellation { get; } = cancellation;
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<string, List<WeakReference<XRShader>>> ShadersByPath =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConditionalWeakTable<XRShader, ShaderDependencies> DependenciesByShader = new();
    private static readonly ConcurrentDictionary<string, PendingChange> PendingChanges =
        new(StringComparer.OrdinalIgnoreCase);

    private static int _debounceMilliseconds = 125;
    private static long _notificationsPublished;
    private static long _staleNotificationsRejected;

    public static int DebounceMilliseconds
    {
        get => Volatile.Read(ref _debounceMilliseconds);
        set => Volatile.Write(ref _debounceMilliseconds, Math.Clamp(value, 0, 5000));
    }

    public static long NotificationsPublished => Interlocked.Read(ref _notificationsPublished);
    public static long StaleNotificationsRejected => Interlocked.Read(ref _staleNotificationsRejected);

    public static void Update(
        XRShader shader,
        string? sourcePath,
        IReadOnlyList<ShaderSourceFileDependency> dependencies)
    {
        ArgumentNullException.ThrowIfNull(shader);
        ArgumentNullException.ThrowIfNull(dependencies);

        HashSet<string> normalizedPaths = new(StringComparer.OrdinalIgnoreCase);
        AddNormalizedPath(normalizedPaths, sourcePath);
        for (int i = 0; i < dependencies.Count; i++)
            AddNormalizedPath(normalizedPaths, dependencies[i].Path);

        string[] replacementPaths = [.. normalizedPaths];
        lock (Sync)
        {
            ShaderDependencies state = DependenciesByShader.GetOrCreateValue(shader);
            RemoveShaderFromPaths(shader, state.Paths);
            state.Paths = replacementPaths;

            for (int i = 0; i < replacementPaths.Length; i++)
            {
                string path = replacementPaths[i];
                if (!ShadersByPath.TryGetValue(path, out List<WeakReference<XRShader>>? shaders))
                {
                    shaders = [];
                    ShadersByPath.Add(path, shaders);
                }

                RemoveDeadAndDuplicateEntries(shaders, shader);
                shaders.Add(new WeakReference<XRShader>(shader));
            }
        }
    }

    public static void QueueFileChange(in ShaderSourceFileChange change)
    {
        string normalizedPath = NormalizePath(change.Path);
        if (normalizedPath.Length == 0)
            return;

        ShaderSourceFileChange normalizedChange = change with
        {
            Path = normalizedPath,
            PreviousPath = NormalizeOptionalPath(change.PreviousPath),
        };

        CancellationTokenSource cancellation = new();
        PendingChange pending = new(normalizedChange, cancellation);
        PendingChanges.AddOrUpdate(
            normalizedPath,
            pending,
            (_, previous) =>
            {
                previous.Cancellation.Cancel();
                previous.Cancellation.Dispose();
                Interlocked.Increment(ref _staleNotificationsRejected);
                return pending;
            });

        _ = ProcessPendingChangeAsync(normalizedPath, pending);
    }

    public static int InvalidateAll(string reason)
    {
        XRShader[] shaders;
        lock (Sync)
        {
            HashSet<XRShader> unique = new(ReferenceEqualityComparer.Instance);
            foreach (List<WeakReference<XRShader>> entries in ShadersByPath.Values)
            {
                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    if (entries[i].TryGetTarget(out XRShader? shader))
                        unique.Add(shader);
                    else
                        entries.RemoveAt(i);
                }
            }

            shaders = [.. unique];
        }

        int invalidated = PublishInvalidationsAtFrameSwap(shaders, reason);
        Interlocked.Add(ref _notificationsPublished, invalidated);
        return invalidated;
    }

    internal static int ProcessFileChangeImmediately(
        in ShaderSourceFileChange change,
        bool publishAtFrameSwap = false)
    {
        int invalidated = InvalidatePath(change.Path, publishAtFrameSwap);
        if (!string.IsNullOrWhiteSpace(change.PreviousPath) &&
            !string.Equals(change.Path, change.PreviousPath, StringComparison.OrdinalIgnoreCase))
        {
            invalidated += InvalidatePath(change.PreviousPath, publishAtFrameSwap);
        }

        Interlocked.Add(ref _notificationsPublished, invalidated);
        return invalidated;
    }

    internal static void ResetForTests()
    {
        foreach (PendingChange pending in PendingChanges.Values)
        {
            pending.Cancellation.Cancel();
            pending.Cancellation.Dispose();
        }

        PendingChanges.Clear();
        lock (Sync)
        {
            ShadersByPath.Clear();
            DependenciesByShader.Clear();
        }

        Interlocked.Exchange(ref _notificationsPublished, 0);
        Interlocked.Exchange(ref _staleNotificationsRejected, 0);
        DebounceMilliseconds = 125;
    }

    private static async Task ProcessPendingChangeAsync(string key, PendingChange pending)
    {
        CancellationToken cancellationToken = pending.Cancellation.Token;
        try
        {
            int delay = DebounceMilliseconds;
            if (delay > 0)
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            if (pending.Change.Kind is ShaderSourceFileChangeKind.Created or ShaderSourceFileChangeKind.Changed or ShaderSourceFileChangeKind.Renamed)
                await WaitForReadableStableFileAsync(pending.Change.Path, cancellationToken).ConfigureAwait(false);

            if (!PendingChanges.TryGetValue(key, out PendingChange? current) ||
                !ReferenceEquals(current, pending))
            {
                Interlocked.Increment(ref _staleNotificationsRejected);
                return;
            }

            ProcessFileChangeImmediately(pending.Change, publishAtFrameSwap: true);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (PendingChanges.TryRemove(new KeyValuePair<string, PendingChange>(key, pending)))
                pending.Cancellation.Dispose();
        }
    }

    private static async Task WaitForReadableStableFileAsync(string path, CancellationToken cancellationToken)
    {
        long previousLength = -1;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                FileInfo file = new(path);
                if (file.Exists)
                {
                    long length = file.Length;
                    using FileStream stream = new(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    if (length == previousLength)
                        return;

                    previousLength = length;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            await Task.Delay(40, cancellationToken).ConfigureAwait(false);
        }
    }

    private static int InvalidatePath(string? path, bool publishAtFrameSwap)
    {
        string normalizedPath = NormalizePath(path);
        if (normalizedPath.Length == 0)
            return 0;

        XRShader[] shaders;
        lock (Sync)
        {
            if (!ShadersByPath.TryGetValue(normalizedPath, out List<WeakReference<XRShader>>? entries))
                return 0;

            HashSet<XRShader> unique = new(ReferenceEqualityComparer.Instance);
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (entries[i].TryGetTarget(out XRShader? shader))
                    unique.Add(shader);
                else
                    entries.RemoveAt(i);
            }

            if (entries.Count == 0)
                ShadersByPath.Remove(normalizedPath);
            shaders = [.. unique];
        }

        return publishAtFrameSwap
            ? PublishInvalidationsAtFrameSwap(shaders, normalizedPath)
            : PublishInvalidations(shaders, normalizedPath);
    }

    /// <summary>
    /// Publishes one dependency-change batch at the collect-visible/render swap
    /// point.
    /// Keeping the whole batch in one job prevents a frame from relinking and
    /// recording against only part of a multi-stage program's invalidation set,
    /// and prevents invalidation from racing the producer for the next package.
    /// </summary>
    private static int PublishInvalidationsAtFrameSwap(
        XRShader[] shaders,
        string reason)
    {
        if (shaders.Length == 0)
            return 0;

        if (!RuntimeRenderingHostServices.HasConcreteHost)
            return PublishInvalidations(shaders, reason);

        IRuntimeRenderSchedulingServices scheduling =
            RuntimeRenderingHostServices.Scheduling;
        if (scheduling.IsFrameSwapThread)
            return PublishInvalidations(shaders, reason);

        scheduling.EnqueueFrameSwapTask(
            () => PublishInvalidations(shaders, reason),
            $"ShaderSourceDependencyIndex.Publish[{reason}]");
        return shaders.Length;
    }

    private static int PublishInvalidations(XRShader[] shaders, string reason)
    {
        for (int i = 0; i < shaders.Length; i++)
            shaders[i].NotifySourceDependencyChanged(reason);

        return shaders.Length;
    }

    private static void RemoveShaderFromPaths(XRShader shader, string[] paths)
    {
        for (int pathIndex = 0; pathIndex < paths.Length; pathIndex++)
        {
            string path = paths[pathIndex];
            if (!ShadersByPath.TryGetValue(path, out List<WeakReference<XRShader>>? entries))
                continue;

            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (!entries[i].TryGetTarget(out XRShader? target) || ReferenceEquals(target, shader))
                    entries.RemoveAt(i);
            }

            if (entries.Count == 0)
                ShadersByPath.Remove(path);
        }
    }

    private static void RemoveDeadAndDuplicateEntries(List<WeakReference<XRShader>> entries, XRShader shader)
    {
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            if (!entries[i].TryGetTarget(out XRShader? target) || ReferenceEquals(target, shader))
                entries.RemoveAt(i);
        }
    }

    private static void AddNormalizedPath(HashSet<string> paths, string? path)
    {
        string normalized = NormalizePath(path);
        if (normalized.Length != 0)
            paths.Add(normalized);
    }

    private static string? NormalizeOptionalPath(string? path)
    {
        string normalized = NormalizePath(path);
        return normalized.Length == 0 ? null : normalized;
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }
}
