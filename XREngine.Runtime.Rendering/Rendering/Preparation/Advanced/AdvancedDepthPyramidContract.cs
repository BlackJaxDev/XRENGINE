using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Shared per-view depth-pyramid identity used by early occlusion, late
/// disocclusion, material work, and compatible secondary consumers.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedDepthPyramidContract(
    ulong ViewHistoryKey,
    uint Width,
    uint Height,
    uint MipCount,
    uint CurrentGeneration,
    uint PreviousGeneration,
    bool PreviousValid,
    bool DepthZeroToOne = true,
    bool ReversedDepth = false)
{
    public bool IsCompatible(ulong historyKey, uint width, uint height)
        => ViewHistoryKey == historyKey &&
           Width == width &&
           Height == height;
}
