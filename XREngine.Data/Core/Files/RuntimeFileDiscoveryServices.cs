namespace XREngine.Data;

/// <summary>Stores the host capability used by portable importers and archive tooling.</summary>
public static class RuntimeFileDiscoveryServices
{
    private static readonly object Gate = new();
    private static IRuntimeFileDiscovery? _current;
    private static long _generation;

    public static IRuntimeFileDiscovery? Current
    {
        get => Volatile.Read(ref _current);
        set
        {
            lock (Gate)
            {
                Volatile.Write(ref _current, value);
                _generation = unchecked(_generation + 1);
            }
        }
    }

    /// <summary>Captures one discovery installation, including an absent provider.</summary>
    public static IRuntimeFileDiscovery? Capture(out long generation)
    {
        lock (Gate)
        {
            generation = _generation;
            return _current;
        }
    }

    /// <summary>Checks the captured installation, including replacement by the same instance.</summary>
    public static bool IsCurrent(IRuntimeFileDiscovery? provider, long generation)
    {
        lock (Gate)
            return ReferenceEquals(_current, provider) && _generation == generation;
    }

    /// <summary>Checks a host file against one raw-read and discovery installation.</summary>
    public static bool HostFileExists(RuntimeAssetReadLease read, string path, string operation)
    {
        ArgumentNullException.ThrowIfNull(read);
        read.EnsureHostFileAccess(operation);
        IRuntimeFileDiscovery? discovery = Capture(out long generation);
        return HostFileExists(read, path, operation, discovery, generation);
    }

    /// <summary>Checks a host file against a captured discovery installation and raw-read lease.</summary>
    public static bool HostFileExists(RuntimeAssetReadLease read, string path, string operation,
        IRuntimeFileDiscovery? discovery, long generation)
    {
        ArgumentNullException.ThrowIfNull(read);
        read.EnsureHostFileAccess(operation);
        if (!IsCurrent(discovery, generation))
            throw new OperationCanceledException("AssetSource.StaleFileDiscovery: the asset file discovery installation changed during the path lookup.");
        bool exists = discovery is IRuntimeHostFileReadBackend files
            ? files.FileExists(path)
            : read.Exists(path);
        using IDisposable publication = read.BeginPublication();
        if (!IsCurrent(discovery, generation))
            throw new OperationCanceledException("AssetSource.StaleFileDiscovery: the asset file discovery installation changed during the path lookup.");
        return exists;
    }

    public static IRuntimeFileDiscovery Required => Current ?? throw new NotSupportedException(
        "File discovery is not installed. The host must provide its storage enumeration capability.");

    /// <summary>Captures the installed host-file read capability. Callers keep their operation owner's host-file admission rules.</summary>
    public static IRuntimeHostFileReadBackend CaptureHostFileReadBackend()
        => Current as IRuntimeHostFileReadBackend ?? throw new NotSupportedException(
            "AssetSource.HostFileReadUnavailable: the installed file system does not provide direct host-file reads.");
}
