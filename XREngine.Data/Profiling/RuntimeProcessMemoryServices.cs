namespace XREngine.Data;

/// <summary>Provides optional process-memory diagnostics without operating-system probes in shared libraries.</summary>
public static class RuntimeProcessMemoryServices
{
    private static Func<long?>? _workingSetBytesReader;

    public static Func<long?>? WorkingSetBytesReader
    {
        get => Volatile.Read(ref _workingSetBytesReader);
        set => Volatile.Write(ref _workingSetBytesReader, value);
    }

    public static long? ReadWorkingSetBytes() => WorkingSetBytesReader?.Invoke();
}
