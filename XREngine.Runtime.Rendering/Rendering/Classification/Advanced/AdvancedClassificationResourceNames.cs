namespace XREngine.Rendering;

/// <summary>
/// Stable resource names for GPU material work classification buffers and debug outputs.
/// </summary>
public static class AdvancedClassificationResourceNames
{
    public const string ScopePrefix = "AdvancedClassification";

    public const string BaseActiveTiles = ScopePrefix + ".ActiveTiles";
    public const string BaseKernelTiles = ScopePrefix + ".KernelTiles";
    public const string BaseCounters = ScopePrefix + ".Counters";
    public const string BaseDispatchArgs = ScopePrefix + ".DispatchArgs";
    public const string BaseDebugOutput = ScopePrefix + ".DebugOutput";

    public static string ActiveTiles(uint slot) => $"{BaseActiveTiles}.Slot{slot}";
    public static string KernelTiles(uint slot) => $"{BaseKernelTiles}.Slot{slot}";
    public static string Counters(uint slot) => $"{BaseCounters}.Slot{slot}";
    public static string DispatchArgs(uint slot) => $"{BaseDispatchArgs}.Slot{slot}";
    public static string DebugOutput => BaseDebugOutput;
}
