using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using XREngine.Core.Files;

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

    private sealed class PendingChange(ShaderSourceFileChange change, CancellationTokenSource cancellation,
        IRuntimeShaderServices? sourceOwner, int serviceVersion)
    {
        public ShaderSourceFileChange Change { get; } = change;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public CancellationToken CancellationToken { get; } = cancellation.Token;
        public IRuntimeShaderServices? SourceOwner { get; } = sourceOwner;
        public int SourceVersion { get; } = sourceOwner?.ShaderAssetCacheVersion ?? 0;
        public int ServiceVersion { get; } = serviceVersion;
    }

    private readonly record struct PreparedRootRefresh(
        XRShader Shader, TextFile Source, long SourceIdentityRevision, string Path, string BaselineText,
        long MutationRevision, long RequestRevision, string RefreshedText);

    private readonly record struct SourceOwnership(
        IRuntimeShaderServices? Service, int ServiceVersion, int SourceVersion);

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
        IReadOnlyList<ShaderSourceFileDependency> dependencies,
        IReadOnlyList<string>? nativeSearchDirectories = null)
    {
        ArgumentNullException.ThrowIfNull(shader);
        ArgumentNullException.ThrowIfNull(dependencies);

        HashSet<string> normalizedPaths = new(StringComparer.OrdinalIgnoreCase);
        AddNormalizedPath(normalizedPaths, sourcePath);
        for (int i = 0; i < dependencies.Count; i++)
            AddNormalizedPath(normalizedPaths, dependencies[i].Path);
        if (nativeSearchDirectories is not null)
            foreach (string directory in nativeSearchDirectories)
                normalizedPaths.Add(NormalizePath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar);

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
        => QueueFileChange(change, RuntimeShaderServices.Current, RuntimeShaderServices.ServiceVersion);

    internal static void QueueFileChange(in ShaderSourceFileChange change,
        IRuntimeShaderServices? sourceOwner, int serviceVersion)
    {
        if (!ShaderSourceResolver.CanAccessHostShaderFiles ||
            !ReferenceEquals(sourceOwner, RuntimeShaderServices.Current) ||
            serviceVersion != RuntimeShaderServices.ServiceVersion)
            return;
        string normalizedPath = NormalizePath(change.Path);
        if (normalizedPath.Length == 0)
            return;

        ShaderSourceFileChange normalizedChange = change with
        {
            Path = normalizedPath,
            PreviousPath = NormalizeOptionalPath(change.PreviousPath),
        };

        CancellationTokenSource cancellation = new();
        PendingChange pending = new(normalizedChange, cancellation, sourceOwner, serviceVersion);
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
        XRShader[] shaders = CollectAllShaders();
        return PublishInvalidationsAtFrameSwap(shaders, reason);
    }

    /// <summary>
    /// Requests a disk refresh for clean root sources and returns the number of selected shaders.
    /// File reads occur on a worker before the existing frame-swap publication boundary.
    /// </summary>
    public static int ReloadAllDiskRoots(string reason)
    {
        if (!ShaderSourceResolver.CanAccessHostShaderFiles)
            return 0;
        IRuntimeShaderServices? service = RuntimeShaderServices.Current;
        SourceOwnership ownership = new(service, RuntimeShaderServices.ServiceVersion, service?.ShaderAssetCacheVersion ?? 0);
        XRShader[] shaders = CollectAllShaders();
        if (shaders.Length != 0)
            _ = Task.Run(() => PrepareManualRootRefreshesAsync(shaders, reason, ownership));
        return shaders.Length;
    }

    private static async Task PrepareManualRootRefreshesAsync(XRShader[] shaders, string reason, SourceOwnership ownership)
    {
        try
        {
            await PrepareAndPublishRootRefreshesAsync(shaders, null, reason, ownership, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ownership.Service?.LogWarning($"Manual shader root reload failed before publication: {ex.Message}");
        }
    }

    private static XRShader[] CollectAllShaders()
    {
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

            return [.. unique];
        }
    }

    internal static int ProcessFileChangeImmediately(
        in ShaderSourceFileChange change,
        bool publishAtFrameSwap = false)
        => ProcessFileChangeImmediately(change, publishAtFrameSwap, sourceOwner: null);

    private static int ProcessFileChangeImmediately(
        in ShaderSourceFileChange change,
        bool publishAtFrameSwap,
        PendingChange? sourceOwner)
    {
        int invalidated = InvalidatePath(change.Path, publishAtFrameSwap, sourceOwner);
        if (!string.IsNullOrWhiteSpace(change.PreviousPath) &&
            !string.Equals(change.Path, change.PreviousPath, StringComparison.OrdinalIgnoreCase))
        {
            invalidated += InvalidatePath(change.PreviousPath, publishAtFrameSwap, sourceOwner);
        }

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
        CancellationToken cancellationToken = pending.CancellationToken;
        try
        {
            int delay = DebounceMilliseconds;
            if (delay > 0)
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            if (!IsSourceOwnerCurrent(pending))
            {
                Interlocked.Increment(ref _staleNotificationsRejected);
                return;
            }
            if (pending.Change.Kind is ShaderSourceFileChangeKind.Created or ShaderSourceFileChangeKind.Changed or ShaderSourceFileChangeKind.Renamed)
                await WaitForReadableStableFileAsync(pending, cancellationToken).ConfigureAwait(false);

            if (!IsSourceOwnerCurrent(pending) ||
                !PendingChanges.TryGetValue(key, out PendingChange? current) ||
                !ReferenceEquals(current, pending))
            {
                Interlocked.Increment(ref _staleNotificationsRejected);
                return;
            }

            XRShader[] shaders = CollectChangedShaders(pending.Change.Path, pending.Change.PreviousPath);
            await PrepareAndPublishRootRefreshesAsync(
                shaders, pending.Change.Path, pending.Change.Path,
                new(pending.SourceOwner, pending.ServiceVersion, pending.SourceVersion), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            pending.SourceOwner?.LogWarning(
                $"Shader source change for '{pending.Change.Path}' failed before publication: {ex.Message}");
        }
        finally
        {
            if (PendingChanges.TryRemove(new KeyValuePair<string, PendingChange>(key, pending)))
                pending.Cancellation.Dispose();
        }
    }

    private static bool IsSourceOwnerCurrent(PendingChange pending)
        => ShaderSourceResolver.CanAccessHostShaderFiles &&
            pending.SourceVersion == (pending.SourceOwner?.ShaderAssetCacheVersion ?? 0) &&
            pending.ServiceVersion == RuntimeShaderServices.ServiceVersion &&
            ReferenceEquals(pending.SourceOwner, RuntimeShaderServices.Current);

    private static async Task WaitForReadableStableFileAsync(PendingChange pending, CancellationToken cancellationToken)
    {
        long previousLength = -1;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsSourceOwnerCurrent(pending))
                return;
            try
            {
                FileInfo file = new(pending.Change.Path);
                if (file.Exists)
                {
                    long length = file.Length;
                    using FileStream stream = new(
                        pending.Change.Path,
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

    private static int InvalidatePath(string? path, bool publishAtFrameSwap, PendingChange? sourceOwner = null)
    {
        string normalizedPath = NormalizePath(path);
        if (normalizedPath.Length == 0)
            return 0;

        XRShader[] shaders = CollectChangedShaders(normalizedPath, null);
        return publishAtFrameSwap
            ? PublishInvalidationsAtFrameSwap(shaders, normalizedPath, sourceOwner)
            : PublishInvalidations(shaders, normalizedPath);
    }

    private static XRShader[] CollectChangedShaders(string? path, string? previousPath)
    {
        string normalizedPath = NormalizePath(path);
        string normalizedPreviousPath = NormalizePath(previousPath);
        lock (Sync)
        {
            HashSet<XRShader> unique = new(ReferenceEqualityComparer.Instance);
            CollectPath(normalizedPath);
            if (normalizedPreviousPath.Length != 0 &&
                !string.Equals(normalizedPath, normalizedPreviousPath, StringComparison.OrdinalIgnoreCase))
                CollectPath(normalizedPreviousPath);
            return [.. unique];

            void CollectPath(string changedPath)
            {
                if (changedPath.Length == 0)
                    return;
                Collect(changedPath);
                // Native module search paths also depend on absence: a newly created
                // higher-priority module must invalidate shaders that imported the old one.
                for (string? directory = Path.GetDirectoryName(changedPath); !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
                    Collect(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar);
            }

            void Collect(string key)
            {
                if (!ShadersByPath.TryGetValue(key, out List<WeakReference<XRShader>>? entries))
                    return;
                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    if (entries[i].TryGetTarget(out XRShader? shader))
                        unique.Add(shader);
                    else
                        entries.RemoveAt(i);
                }
                if (entries.Count == 0)
                    ShadersByPath.Remove(key);
            }
        }
    }

    private static async Task PrepareAndPublishRootRefreshesAsync(
        XRShader[] shaders, string? changedPath, string reason, SourceOwnership ownership, CancellationToken cancellationToken)
    {
        List<PreparedRootRefresh> prepared = [];
        for (int i = 0; i < shaders.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryAdmitSourceOwner(ownership))
                return;
            XRShader shader = shaders[i];
            if (!shader.CanPublishSourceChanges)
                continue;
            TextFile source = shader.Source;
            long sourceIdentityRevision = shader.SourceIdentityRevision;
            string? path = changedPath ?? source.FilePath;
            if (string.IsNullOrWhiteSpace(path) ||
                !source.TryCaptureDiskRefresh(path, out string baselineText,
                    out long mutationRevision, out long requestRevision, out Encoding encoding))
                continue;

            try
            {
                string refreshedText = await File.ReadAllTextAsync(path, encoding, cancellationToken).ConfigureAwait(false);
                prepared.Add(new PreparedRootRefresh(
                    shader, source, sourceIdentityRevision, path, baselineText,
                    mutationRevision, requestRevision, refreshedText));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                ownership.Service?.LogWarning(
                    $"Failed to refresh disk-backed shader root '{path}': {ex.Message}");
            }
        }

        PreparedRootRefresh[] refreshes = [.. prepared];
        cancellationToken.ThrowIfCancellationRequested();
        PublishPreparedRootRefreshesAtFrameSwap(shaders, refreshes, reason, ownership);
    }

    private static void PublishPreparedRootRefreshesAtFrameSwap(
        XRShader[] shaders, PreparedRootRefresh[] refreshes, string reason, SourceOwnership ownership)
    {
        if (shaders.Length == 0)
            return;

        if (!RuntimeRenderingHostServices.HasConcreteHost ||
            RuntimeRenderingHostServices.Scheduling.IsFrameSwapThread)
        {
            PublishPreparedRootRefreshes(shaders, refreshes, reason, ownership);
            return;
        }

        RuntimeRenderingHostServices.Scheduling.EnqueueFrameSwapTask(
            () => PublishPreparedRootRefreshes(shaders, refreshes, reason, ownership),
            $"ShaderSourceDependencyIndex.Refresh[{reason}]");
    }

    private static void PublishPreparedRootRefreshes(
        XRShader[] shaders, PreparedRootRefresh[] refreshes, string reason, SourceOwnership ownership)
    {
        int published = 0;
        long[] revisions = new long[shaders.Length];
        for (int i = 0; i < shaders.Length; i++)
            revisions[i] = shaders[i].SourceRevision;

        for (int i = 0; i < refreshes.Length; i++)
        {
            if (!TryAdmitSourceOwner(ownership))
                return;
            PreparedRootRefresh refresh = refreshes[i];
            refresh.Shader.TryApplyDiskRootRefresh(
                refresh.Source, refresh.SourceIdentityRevision, refresh.Path, refresh.BaselineText,
                refresh.MutationRevision, refresh.RequestRevision, refresh.RefreshedText);
        }

        for (int i = 0; i < shaders.Length; i++)
        {
            if (!shaders[i].CanPublishSourceChanges)
                continue;
            if (shaders[i].SourceRevision == revisions[i])
            {
                if (!TryAdmitSourceOwner(ownership))
                    break;
                shaders[i].NotifySourceDependencyChanged(reason, ownership.Service);
            }
            published++;
        }
        Interlocked.Add(ref _notificationsPublished, published);
    }

    private static bool TryAdmitSourceOwner(in SourceOwnership ownership)
    {
        if (RuntimeShaderServices.TryAdmitSourceInvalidation(
                ownership.Service, ownership.ServiceVersion, ownership.SourceVersion))
            return true;
        Interlocked.Increment(ref _staleNotificationsRejected);
        return false;
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
        string reason,
        PendingChange? sourceOwner = null)
    {
        if (shaders.Length == 0)
            return 0;

        if (!RuntimeRenderingHostServices.HasConcreteHost)
            return PublishOwnedInvalidations(shaders, reason, sourceOwner);

        IRuntimeRenderSchedulingServices scheduling =
            RuntimeRenderingHostServices.Scheduling;
        if (scheduling.IsFrameSwapThread)
            return PublishOwnedInvalidations(shaders, reason, sourceOwner);

        scheduling.EnqueueFrameSwapTask(
            () => PublishOwnedInvalidations(shaders, reason, sourceOwner),
            $"ShaderSourceDependencyIndex.Publish[{reason}]");
        return shaders.Length;
    }

    private static int PublishOwnedInvalidations(XRShader[] shaders, string reason, PendingChange? sourceOwner)
    {
        if (sourceOwner is null)
            return PublishInvalidations(shaders, reason);

        int invalidated = 0;
        for (int i = 0; i < shaders.Length; i++)
        {
            if (!shaders[i].CanPublishSourceChanges)
                continue;
            if (!RuntimeShaderServices.TryAdmitSourceInvalidation(
                sourceOwner.SourceOwner, sourceOwner.ServiceVersion, sourceOwner.SourceVersion))
            {
                Interlocked.Increment(ref _staleNotificationsRejected);
                break;
            }

            // One admitted notification keeps its synchronous property/event
            // semantics. Reentrant retirement stops the next shader in this job.
            shaders[i].NotifySourceDependencyChanged(reason, sourceOwner.SourceOwner);
            invalidated++;
        }
        Interlocked.Add(ref _notificationsPublished, invalidated);
        return invalidated;
    }

    private static int PublishInvalidations(XRShader[] shaders, string reason)
    {
        int published = 0;
        for (int i = 0; i < shaders.Length; i++)
        {
            if (!shaders[i].CanPublishSourceChanges)
                continue;
            shaders[i].NotifySourceDependencyChanged(reason);
            published++;
        }

        Interlocked.Add(ref _notificationsPublished, published);
        return published;
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
