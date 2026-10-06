namespace XREngine.Data;

/// <summary>Stores the host's asset file output service.</summary>
public static class HostAssetFileOutputServices
{
    private static IHostAssetFileOutput? _current;

    public static IHostAssetFileOutput? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }

    /// <summary>Gets the installed file output service.</summary>
    public static IHostAssetFileOutput Required => Current ??
        throw new NotSupportedException(
            "HostAssetFileOutput.BackendUnavailable: install an asset file output service before saving to a host path.");
}
