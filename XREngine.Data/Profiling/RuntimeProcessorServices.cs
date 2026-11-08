namespace XREngine.Data;

/// <summary>
/// Provides the number of logical processors that the process can use now, without operating-system probes in shared
/// libraries. <see cref="Environment.ProcessorCount"/> is read one time at startup, so it does not see an affinity mask
/// that a launcher or a user sets later; a desktop host installs a reader that does.
/// </summary>
public static class RuntimeProcessorServices
{
    private static Func<int?>? _effectiveProcessorCountReader;

    /// <summary>The optional host reader. It returns null when it cannot read the value.</summary>
    public static Func<int?>? EffectiveProcessorCountReader
    {
        get => Volatile.Read(ref _effectiveProcessorCountReader);
        set => Volatile.Write(ref _effectiveProcessorCountReader, value);
    }

    /// <summary>
    /// Gets the processor count from the host reader, or <see cref="Environment.ProcessorCount"/> when no reader is
    /// installed or it gives no value. The result is at least 1.
    /// </summary>
    public static int ReadEffectiveProcessorCount()
    {
        int? count = EffectiveProcessorCountReader?.Invoke();
        return Math.Max(1, count ?? Environment.ProcessorCount);
    }
}
