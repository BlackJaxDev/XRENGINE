namespace XREngine.Data;

/// <summary>Stores the host capability used by portable importers and archive tooling.</summary>
public static class RuntimeFileDiscoveryServices
{
    private static IRuntimeFileDiscovery? _current;

    public static IRuntimeFileDiscovery? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }

    public static IRuntimeFileDiscovery Required => Current ?? throw new NotSupportedException(
        "File discovery is not installed. The host must provide its storage enumeration capability.");

    /// <summary>Captures the installed host-file read capability. Callers keep their operation owner's host-file admission rules.</summary>
    public static IRuntimeHostFileReadBackend CaptureHostFileReadBackend()
        => Current as IRuntimeHostFileReadBackend ?? throw new NotSupportedException(
            "AssetSource.HostFileReadUnavailable: the installed file system does not provide direct host-file reads.");
}
