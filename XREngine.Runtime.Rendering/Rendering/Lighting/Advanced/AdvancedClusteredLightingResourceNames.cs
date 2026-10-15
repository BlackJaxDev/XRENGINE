namespace XREngine.Rendering;

/// <summary>
/// Stable resource names for clustered lighting froxel buffers.
/// </summary>
/// <remarks>
/// The slot builders return cached strings for slots below
/// <see cref="AdvancedFrameSlotContract.DefaultSlotCount"/>, so frame-rate callers do not allocate.
/// Other slots get a new formatted string with the same text pattern.
/// </remarks>
public static class AdvancedClusteredLightingResourceNames
{
    public const string ScopePrefix = "AdvancedClusteredLighting";

    public const string BaseFroxelGrid = ScopePrefix + ".FroxelGrid";
    public const string BaseFroxelDecalGrid = ScopePrefix + ".FroxelDecalGrid";
    public const string BaseLightIndexList = ScopePrefix + ".LightIndexList";
    public const string BaseDecalIndexList = ScopePrefix + ".DecalIndexList";
    public const string BaseLightingCounters = ScopePrefix + ".Counters";

    /// <summary>
    /// Number of slots that have a cached name for each slot builder.
    /// </summary>
    private const uint CachedSlotCount = AdvancedFrameSlotContract.DefaultSlotCount;

    private static readonly string[] FroxelGridSlotNames = CreateSlotNames(BaseFroxelGrid);
    private static readonly string[] FroxelDecalGridSlotNames = CreateSlotNames(BaseFroxelDecalGrid);
    private static readonly string[] LightIndexListSlotNames = CreateSlotNames(BaseLightIndexList);
    private static readonly string[] DecalIndexListSlotNames = CreateSlotNames(BaseDecalIndexList);
    private static readonly string[] LightingCountersSlotNames = CreateSlotNames(BaseLightingCounters);

    /// <summary>
    /// Gets the light froxel grid buffer name for a frame slot.
    /// </summary>
    public static string FroxelGrid(uint slot)
        => slot < CachedSlotCount ? FroxelGridSlotNames[slot] : FormatSlotName(BaseFroxelGrid, slot);

    /// <summary>
    /// Gets the decal froxel grid buffer name for a frame slot.
    /// </summary>
    public static string FroxelDecalGrid(uint slot)
        => slot < CachedSlotCount ? FroxelDecalGridSlotNames[slot] : FormatSlotName(BaseFroxelDecalGrid, slot);

    /// <summary>
    /// Gets the light index list buffer name for a frame slot.
    /// </summary>
    public static string LightIndexList(uint slot)
        => slot < CachedSlotCount ? LightIndexListSlotNames[slot] : FormatSlotName(BaseLightIndexList, slot);

    /// <summary>
    /// Gets the decal index list buffer name for a frame slot.
    /// </summary>
    public static string DecalIndexList(uint slot)
        => slot < CachedSlotCount ? DecalIndexListSlotNames[slot] : FormatSlotName(BaseDecalIndexList, slot);

    /// <summary>
    /// Gets the clustered lighting counter buffer name for a frame slot.
    /// </summary>
    public static string LightingCounters(uint slot)
        => slot < CachedSlotCount ? LightingCountersSlotNames[slot] : FormatSlotName(BaseLightingCounters, slot);

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
