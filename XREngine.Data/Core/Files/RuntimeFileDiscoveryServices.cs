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

    public static IRuntimeFileDiscovery Required => Current ?? throw new NotSupportedException(
        "File discovery is not installed. The host must provide its storage enumeration capability.");

    /// <summary>Captures the installed host-file read capability. Callers keep their operation owner's host-file admission rules.</summary>
    public static IRuntimeHostFileReadBackend CaptureHostFileReadBackend()
        => Current as IRuntimeHostFileReadBackend ?? throw new NotSupportedException(
            "AssetSource.HostFileReadUnavailable: the installed file system does not provide direct host-file reads.");
}
