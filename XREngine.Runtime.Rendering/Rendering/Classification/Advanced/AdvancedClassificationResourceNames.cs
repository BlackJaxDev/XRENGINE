namespace XREngine.Rendering;

/// <summary>
/// Stable resource names for GPU material work classification buffers and debug outputs.
/// </summary>
/// <remarks>
/// The slot builders return cached strings for slots below
/// <see cref="AdvancedFrameSlotContract.DefaultSlotCount"/>, so frame-rate callers do not allocate.
/// Other slots get a new formatted string with the same text pattern.
/// </remarks>
public static class AdvancedClassificationResourceNames
{
    public const string ScopePrefix = "AdvancedClassification";

    public const string BaseActiveTiles = ScopePrefix + ".ActiveTiles";
    public const string BaseKernelTiles = ScopePrefix + ".KernelTiles";
    public const string BaseCounters = ScopePrefix + ".Counters";
    public const string BaseDispatchArgs = ScopePrefix + ".DispatchArgs";
    public const string BaseKernelCounts = ScopePrefix + ".KernelCounts";
    public const string BaseDebugOutput = ScopePrefix + ".DebugOutput";

    /// <summary>
    /// Number of slots that have a cached name for each slot builder.
    /// </summary>
    private const uint CachedSlotCount = AdvancedFrameSlotContract.DefaultSlotCount;

    private static readonly string[] ActiveTilesSlotNames = CreateSlotNames(BaseActiveTiles);
    private static readonly string[] KernelTilesSlotNames = CreateSlotNames(BaseKernelTiles);
    private static readonly string[] CountersSlotNames = CreateSlotNames(BaseCounters);
    private static readonly string[] DispatchArgsSlotNames = CreateSlotNames(BaseDispatchArgs);
    private static readonly string[] KernelCountsSlotNames = CreateSlotNames(BaseKernelCounts);

    /// <summary>
    /// Gets the active tile list buffer name for a frame slot.
    /// </summary>
    public static string ActiveTiles(uint slot)
        => slot < CachedSlotCount ? ActiveTilesSlotNames[slot] : FormatSlotName(BaseActiveTiles, slot);

    /// <summary>
    /// Gets the per-kernel tile list buffer name for a frame slot.
    /// </summary>
    public static string KernelTiles(uint slot)
        => slot < CachedSlotCount ? KernelTilesSlotNames[slot] : FormatSlotName(BaseKernelTiles, slot);

    /// <summary>
    /// Gets the classification counter buffer name for a frame slot.
    /// </summary>
    public static string Counters(uint slot)
        => slot < CachedSlotCount ? CountersSlotNames[slot] : FormatSlotName(BaseCounters, slot);

    /// <summary>
    /// Gets the indirect dispatch argument buffer name for a frame slot.
    /// </summary>
    public static string DispatchArgs(uint slot)
        => slot < CachedSlotCount ? DispatchArgsSlotNames[slot] : FormatSlotName(BaseDispatchArgs, slot);

    /// <summary>
    /// Gets the per-kernel tile count buffer name for a frame slot.
    /// </summary>
    public static string KernelCounts(uint slot)
        => slot < CachedSlotCount ? KernelCountsSlotNames[slot] : FormatSlotName(BaseKernelCounts, slot);

    public static string DebugOutput => BaseDebugOutput;

    /// <summary>
    /// Builds the cached names for all slots below <see cref="CachedSlotCount"/>.
    /// </summary>
    private static string[] CreateSlotNames(string baseName)
    {
        string[] names = new string[CachedSlotCount];
        for (uint slot = 0u; slot < CachedSlotCount; slot++)
            names[slot] = FormatSlotName(baseName, slot);
        return names;
    }

    /// <summary>
    /// Formats a slot name. Each call allocates a new string.
    /// </summary>
    private static string FormatSlotName(string baseName, uint slot) => $"{baseName}.Slot{slot}";
}
