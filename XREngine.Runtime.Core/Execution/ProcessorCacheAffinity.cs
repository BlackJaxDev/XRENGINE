using System.Numerics;

namespace XREngine.Execution;

/// <summary>
/// Restricts the process to the logical processors that share the largest L3 cache when the
/// L3 caches of the CPU have different sizes. CPUs with a stacked cache on one core complex
/// run the render and eye recording threads much faster on that complex, and the OS scheduler
/// does not always place them there. Detection reads the cache topology from the OS, so it
/// needs no list of CPU vendors or models. A CPU whose L3 caches have equal sizes is not changed.
/// </summary>
/// <remarks>
/// Hybrid CPUs that differ only in core efficiency class (performance and efficiency cores)
/// are reported but not restricted: the OS thread director already places foreground threads on
/// performance cores, and a process mask would take the efficiency cores from background workers.
/// </remarks>
public static class ProcessorCacheAffinity
{
    private const int MinimumLogicalProcessors = 2;

    /// <summary>
    /// Selects the logical processors that share the largest L3 cache. No restriction applies
    /// when there are fewer than two L3 caches, when all have the same size, when the largest
    /// caches span more than one processor group, or when they hold too few processors.
    /// </summary>
    public static ProcessorCacheAffinitySelection Select(ReadOnlySpan<ProcessorCacheGroup> l3Caches)
    {
        if (l3Caches.Length < 2)
            return NoRestriction($"{l3Caches.Length} L3 cache(s); no asymmetry");

        uint largest = 0;
        uint smallest = uint.MaxValue;
        for (int index = 0; index < l3Caches.Length; index++)
        {
            largest = Math.Max(largest, l3Caches[index].SizeBytes);
            smallest = Math.Min(smallest, l3Caches[index].SizeBytes);
        }

        if (largest == smallest)
            return NoRestriction($"{l3Caches.Length} L3 caches of {FormatSize(largest)}; no asymmetry");

        ushort group = 0;
        ulong mask = 0UL;
        bool groupSet = false;
        for (int index = 0; index < l3Caches.Length; index++)
        {
            ProcessorCacheGroup cache = l3Caches[index];
            if (cache.SizeBytes != largest)
                continue;
            if (groupSet && cache.Group != group)
                return NoRestriction("the largest L3 caches span more than one processor group");

            group = cache.Group;
            groupSet = true;
            mask |= cache.Mask;
        }

        int count = BitOperations.PopCount(mask);
        if (count < MinimumLogicalProcessors)
            return NoRestriction($"the largest L3 cache serves only {count} logical processor(s)");

        return new ProcessorCacheAffinitySelection(
            true,
            group,
            mask,
            count,
            $"L3 sizes differ ({FormatSize(smallest)} to {FormatSize(largest)}); " +
            $"using the {count} logical processors of the {FormatSize(largest)} L3 (group {group}, mask 0x{mask:X})");
    }

    /// <summary>
    /// Applies the cache selection to the current process through the installed platform
    /// capability. Returns the number of logical processors that the process may use after
    /// the call. Without a platform capability the affinity is unchanged.
    /// </summary>
    /// <param name="enabled">False keeps the process affinity and only reports the topology.</param>
    /// <param name="log">Receives one line that describes the decision.</param>
    public static int ApplyToCurrentProcess(bool enabled, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (ProcessAffinityApplier is { } applier)
            return applier(enabled, log);

        log("[CPU affinity] No platform capability reads the cache topology; affinity unchanged.");
        return Environment.ProcessorCount;
    }

    /// <summary>
    /// Platform capability that reads the cache topology, applies <see cref="Select"/> to the
    /// current process, and returns the usable logical processor count.
    /// </summary>
    public static Func<bool, Action<string>, int>? ProcessAffinityApplier { get; set; }

    private static ProcessorCacheAffinitySelection NoRestriction(string reason)
        => new(false, 0, 0UL, 0, reason);

    private static string FormatSize(uint bytes)
        => bytes >= 1024u * 1024u
            ? $"{bytes / (1024u * 1024u)} MB"
            : $"{bytes / 1024u} KB";
}
