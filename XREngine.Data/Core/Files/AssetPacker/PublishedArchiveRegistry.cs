namespace XREngine.Core.Files;

/// <summary>
/// Owns one open <see cref="PublishedArchiveHandle"/> per archive path and maps each published
/// content root to its archive. Archives open on first use and stay open until the root is
/// reconfigured, content is swapped, or the runtime shuts down.
/// </summary>
public static class PublishedArchiveRegistry
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, PublishedArchiveHandle> ByPath = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string?[] RootPaths = new string?[3];
    private static long _openCount;

    /// <summary>Total archive opens since process start. The load path should keep this at one per root.</summary>
    public static long TotalOpenCount => Interlocked.Read(ref _openCount);

    /// <summary>Number of archives currently open.</summary>
    public static int OpenHandleCount
    {
        get
        {
            lock (Sync)
                return ByPath.Count;
        }
    }

    /// <summary>
    /// Assigns the archive for a content root. A different path closes the previous archive for
    /// that root; the new archive opens lazily on first use. Null clears the root.
    /// </summary>
    public static void ConfigureRoot(EPublishedContentRoot root, string? archivePath)
    {
        string? normalized = string.IsNullOrWhiteSpace(archivePath) ? null : Path.GetFullPath(archivePath);
        PublishedArchiveHandle? toClose = null;
        lock (Sync)
        {
            string? previous = RootPaths[(int)root];
            RootPaths[(int)root] = normalized;
            if (previous is not null
                && !string.Equals(previous, normalized, StringComparison.OrdinalIgnoreCase)
                && !IsReferencedByAnotherRoot(previous, (int)root)
                && ByPath.Remove(previous, out PublishedArchiveHandle? handle))
            {
                toClose = handle;
            }
        }

        toClose?.Dispose();
    }

    /// <summary>Returns the configured archive path for a root, or null.</summary>
    public static string? GetRootPath(EPublishedContentRoot root)
    {
        lock (Sync)
            return RootPaths[(int)root];
    }

    /// <summary>Opens or returns the archive for a root. False when the root has no archive.</summary>
    public static bool TryGetRoot(EPublishedContentRoot root, out PublishedArchiveHandle handle)
    {
        lock (Sync)
        {
            string? path = RootPaths[(int)root];
            if (path is null)
            {
                handle = null!;
                return false;
            }
            handle = GetOrOpen(path);
            return true;
        }
    }

    /// <summary>Opens the archive at <paramref name="archivePath"/> once and returns the shared handle.</summary>
    public static PublishedArchiveHandle GetOrOpen(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        string fullPath = Path.GetFullPath(archivePath);
        lock (Sync)
        {
            if (ByPath.TryGetValue(fullPath, out PublishedArchiveHandle? existing) && !existing.IsDisposed)
                return existing;

            PublishedArchiveHandle handle = PublishedArchiveHandle.Open(fullPath);
            ByPath[fullPath] = handle;
            Interlocked.Increment(ref _openCount);
            return handle;
        }
    }

    /// <summary>Closes one archive if it is open. Roots that referenced it reopen it on next use.</summary>
    public static bool Close(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        string fullPath = Path.GetFullPath(archivePath);
        PublishedArchiveHandle? handle;
        lock (Sync)
        {
            if (!ByPath.Remove(fullPath, out handle))
                return false;
        }

        handle.Dispose();
        return true;
    }

    /// <summary>Closes every open archive. Root paths are kept so the archives reopen on next use.</summary>
    public static void CloseAll()
    {
        PublishedArchiveHandle[] handles;
        lock (Sync)
        {
            handles = new PublishedArchiveHandle[ByPath.Count];
            ByPath.Values.CopyTo(handles, 0);
            ByPath.Clear();
        }

        for (int i = 0; i < handles.Length; i++)
            handles[i].Dispose();
    }

    /// <summary>Closes every archive and clears every root. Used at runtime shutdown and content swap.</summary>
    public static void Reset()
    {
        lock (Sync)
        {
            for (int i = 0; i < RootPaths.Length; i++)
                RootPaths[i] = null;
            CloseAll();
        }
    }

    private static bool IsReferencedByAnotherRoot(string path, int excludingRoot)
    {
        for (int i = 0; i < RootPaths.Length; i++)
        {
            if (i != excludingRoot && string.Equals(RootPaths[i], path, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
