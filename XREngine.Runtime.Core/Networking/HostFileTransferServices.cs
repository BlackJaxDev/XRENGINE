namespace XREngine.Networking;

/// <summary>Stores the host's explicitly installed file-transfer backend.</summary>
public static class HostFileTransferServices
{
    private static IHostFileTransferBackend? _current;

    public static IHostFileTransferBackend? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }

    public static IHostFileTransferBackend Required => Current ??
        throw new NotSupportedException(
            "NetworkFileTransfer.HostFileUnavailable: this host cannot transfer operating-system file paths; use an already opened stream with an available transport.");
}
