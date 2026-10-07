namespace XREngine.Rendering;

/// <summary>Installs the spill storage supplied by the application host.</summary>
public static class XRBufferSpillStorageServices
{
    private static IXRBufferSpillStorage? _current;

    public static IXRBufferSpillStorage? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }

    public static IXRBufferSpillStorage Required => Current ?? throw new NotSupportedException(
        "Buffer spill storage is not installed. Register a host buffer spill storage provider before using buffer spill mapping.");
}
