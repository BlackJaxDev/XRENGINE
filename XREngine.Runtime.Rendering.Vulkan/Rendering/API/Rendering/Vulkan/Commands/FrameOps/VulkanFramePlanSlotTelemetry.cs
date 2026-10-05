namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Process-wide counters for frame-plan slot reuse. A slot index is rebuilt in
/// place unless its plan is still pinned by an in-flight frame; then a retired
/// spare workspace takes its place. These counters show how often that happens
/// and how close the spares came to running out, which decides how many spare
/// workspaces must be provisioned ahead of time.
/// </summary>
internal static class VulkanFramePlanSlotTelemetry
{
    private static long s_buildCount;
    private static long s_replacementCount;
    private static long s_spareCreatedCount;
    private static int s_minimumAvailableSpares = int.MaxValue;

    internal static long BuildCount => Interlocked.Read(ref s_buildCount);
    internal static long ReplacementCount => Interlocked.Read(ref s_replacementCount);
    /// <summary>Spare workspaces created on demand after the initial provisioning.</summary>
    internal static long SpareCreatedCount => Interlocked.Read(ref s_spareCreatedCount);

    /// <summary>The fewest spares left after a replacement, or -1 if none happened.</summary>
    internal static int MinimumAvailableSpares
    {
        get
        {
            int value = Volatile.Read(ref s_minimumAvailableSpares);
            return value == int.MaxValue ? -1 : value;
        }
    }

    internal static void RecordSpareCreated()
        => Interlocked.Increment(ref s_spareCreatedCount);

    internal static void RecordBuild()
        => Interlocked.Increment(ref s_buildCount);

    internal static void RecordReplacement(int sparesLeft)
    {
        Interlocked.Increment(ref s_replacementCount);
        int current = Volatile.Read(ref s_minimumAvailableSpares);
        while (sparesLeft < current)
        {
            int observed = Interlocked.CompareExchange(ref s_minimumAvailableSpares, sparesLeft, current);
            if (observed == current)
                break;
            current = observed;
        }
    }

    /// <summary>Summary for diagnostics and MCP inspection.</summary>
    internal static string Describe()
        => $"builds={BuildCount} replacements={ReplacementCount} sparesCreated={SpareCreatedCount} minimumAvailableSpares={MinimumAvailableSpares}";
}
