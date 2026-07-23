namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Defines the bounded growth policy used by renderer-owned Vulkan query arenas.
/// </summary>
public static class VulkanQueryArenaPolicy
{
    public const uint DefaultChunkCapacity = 256u;
    public const int MaxChunksPerKey = 16;

    /// <summary>
    /// Returns the smallest supported chunk that can contain a contiguous query range.
    /// </summary>
    public static uint ResolveChunkCapacity(uint requiredQueryCount)
        => Math.Max(DefaultChunkCapacity, RoundUpPowerOfTwo(requiredQueryCount));

    /// <summary>
    /// Returns whether another chunk may be added for a compatibility key.
    /// </summary>
    public static bool CanGrow(int existingChunkCount)
        => existingChunkCount >= 0 && existingChunkCount < MaxChunksPerKey;

    private static uint RoundUpPowerOfTwo(uint value)
    {
        if (value <= 1u)
            return 1u;

        value--;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        return value + 1u;
    }
}
