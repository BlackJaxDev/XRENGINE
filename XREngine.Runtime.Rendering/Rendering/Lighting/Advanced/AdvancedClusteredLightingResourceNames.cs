namespace XREngine.Rendering;

/// <summary>
/// Stable resource names for clustered lighting froxel buffers.
/// </summary>
public static class AdvancedClusteredLightingResourceNames
{
    public const string ScopePrefix = "AdvancedClusteredLighting";

    public const string BaseFroxelGrid = ScopePrefix + ".FroxelGrid";
    public const string BaseFroxelDecalGrid = ScopePrefix + ".FroxelDecalGrid";
    public const string BaseLightIndexList = ScopePrefix + ".LightIndexList";
    public const string BaseDecalIndexList = ScopePrefix + ".DecalIndexList";
    public const string BaseLightingCounters = ScopePrefix + ".Counters";

    public static string FroxelGrid(uint slot) => $"{BaseFroxelGrid}.Slot{slot}";
    public static string FroxelDecalGrid(uint slot) => $"{BaseFroxelDecalGrid}.Slot{slot}";
    public static string LightIndexList(uint slot) => $"{BaseLightIndexList}.Slot{slot}";
    public static string DecalIndexList(uint slot) => $"{BaseDecalIndexList}.Slot{slot}";
    public static string LightingCounters(uint slot) => $"{BaseLightingCounters}.Slot{slot}";
}
