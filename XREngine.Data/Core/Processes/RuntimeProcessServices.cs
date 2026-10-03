namespace XREngine.Data;

/// <summary>Provides the external-tool capability explicitly installed by the application host.</summary>
public static class RuntimeProcessServices
{
    private static IRuntimeProcessRunner? _current;

    public static IRuntimeProcessRunner? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }

    public static IRuntimeProcessRunner Require()
        => Current ?? throw new NotSupportedException(
            "External process execution is unavailable. Install a platform process runner in the application host.");
}
