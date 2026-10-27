namespace XREngine.Rendering;

/// <summary>Hashes stable handle words for render scheduling and visibility lookup.</summary>
internal static class AdvancedGpuHandleHash
{
    /// <summary>Combines the slot index and generation without temporary storage.</summary>
    [global::Engine.Diagnostics.HotPath]
    internal static uint Compute(uint index, uint generation)
    {
        uint value = index * 0x9E3779B9u;
        value ^= generation + 0x85EBCA6Bu + (value << 6) + (value >> 2);
        value ^= value >> 16;
        value *= 0x7FEB352Du;
        return value ^ (value >> 15);
    }
}
