namespace XREngine.Data;

/// <summary>Holds the optional host capability for dynamic runtime assembly loading.</summary>
public static class RuntimeAssemblyLoadingServices
{
    private static IRuntimeAssemblyLoader? _current;

    public static IRuntimeAssemblyLoader? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }
}
