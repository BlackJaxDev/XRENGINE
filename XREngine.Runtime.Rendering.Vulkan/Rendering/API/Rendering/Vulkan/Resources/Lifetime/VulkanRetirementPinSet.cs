namespace XREngine.Rendering.Vulkan;
internal sealed class VulkanRetirementPinSet
{
    private readonly VulkanPinnedResourceGeneration[] _resources;
    private VulkanRetirementPinSet(VulkanPinnedResourceGeneration[] resources) => _resources = resources;
    internal ReadOnlySpan<VulkanPinnedResourceGeneration> Resources => _resources;
    internal int Count => _resources.Length;
    internal static VulkanRetirementPinSet Single(VulkanResourceLifetimeKey key, ulong generation) => new([new(key, generation)]);
    internal static VulkanRetirementPinSet? Merge(VulkanRetirementPinSet? first, VulkanRetirementPinSet? second)
    {
        if (first is null) return second;
        if (second is null || ReferenceEquals(first, second)) return first;
        VulkanPinnedResourceGeneration[] merged = new VulkanPinnedResourceGeneration[first._resources.Length + second._resources.Length];
        first._resources.CopyTo(merged, 0); int count = first._resources.Length;
        foreach (VulkanPinnedResourceGeneration candidate in second._resources)
        { bool duplicate = false; for (int i = 0; i < count; i++) if (merged[i] == candidate) { duplicate = true; break; } if (!duplicate) merged[count++] = candidate; }
        if (count != merged.Length) Array.Resize(ref merged, count); return new VulkanRetirementPinSet(merged);
    }
}
