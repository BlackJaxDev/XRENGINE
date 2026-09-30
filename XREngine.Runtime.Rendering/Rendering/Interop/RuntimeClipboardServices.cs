namespace XREngine.Rendering;

/// <summary>Stores the optional clipboard capability installed by the host.</summary>
public static class RuntimeClipboardServices
{
    private static IRuntimeClipboardServices? _current;
    public static IRuntimeClipboardServices? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }
}
