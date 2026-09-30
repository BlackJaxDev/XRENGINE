namespace XREngine.Data;

/// <summary>Stores the host's file-mapping capability without an OS dependency.</summary>
public static class FileMappingServices
{
    private static IFileMappingBackend? _backend;
    public static IFileMappingBackend? Backend
    {
        get => Volatile.Read(ref _backend);
        set => Volatile.Write(ref _backend, value);
    }
    public static IFileMappingBackend Required => Backend ??
        throw new NotSupportedException("File mapping is not installed. Install the desktop file-mapping backend in this host.");
}
