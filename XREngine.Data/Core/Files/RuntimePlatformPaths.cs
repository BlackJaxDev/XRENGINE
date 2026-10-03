namespace XREngine.Data;

/// <summary>Provides the platform paths explicitly installed by the host.</summary>
public static class RuntimePlatformPaths
{
    private static IRuntimePlatformPaths? _current;

    public static IRuntimePlatformPaths? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }

    public static string GetFolderPath(Environment.SpecialFolder folder)
        => (Current ?? throw new NotSupportedException(
            "Platform folders are not installed. Configure a platform path provider in the host.")).GetFolderPath(folder);
}
