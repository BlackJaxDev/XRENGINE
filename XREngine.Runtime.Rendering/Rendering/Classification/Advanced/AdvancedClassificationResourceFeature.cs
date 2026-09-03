using System;

namespace XREngine.Rendering;

/// <summary>
/// Bitmask selecting optional classification resources in a render pipeline resource profile.
/// </summary>
[Flags]
public enum AdvancedClassificationResourceFeature : uint
{
    None = 0,
    ActiveTiles = 1u << 0,
    KernelTiles = 1u << 1,
    IndirectDispatch = 1u << 2,
    Counters = 1u << 3,
    CompactPixels = 1u << 4,
    DebugOutput = 1u << 5,

    Standard = ActiveTiles | KernelTiles | IndirectDispatch | Counters,
    All = Standard | CompactPixels | DebugOutput,
}
