namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal sealed class VulkanRetirementPinSet
    {
        private readonly VulkanPinnedResourceGeneration[] _resources;

        private VulkanRetirementPinSet(VulkanPinnedResourceGeneration[] resources)
            => _resources = resources;

        internal ReadOnlySpan<VulkanPinnedResourceGeneration> Resources => _resources;
        internal int Count => _resources.Length;

        internal static VulkanRetirementPinSet Single(
            VulkanResourceLifetimeKey key,
            ulong generation)
            => new([new VulkanPinnedResourceGeneration(key, generation)]);

        internal static VulkanRetirementPinSet? Merge(
            VulkanRetirementPinSet? first,
            VulkanRetirementPinSet? second)
        {
            if (first is null)
                return second;
            if (second is null || ReferenceEquals(first, second))
                return first;

            ReadOnlySpan<VulkanPinnedResourceGeneration> firstResources = first.Resources;
            ReadOnlySpan<VulkanPinnedResourceGeneration> secondResources = second.Resources;
            VulkanPinnedResourceGeneration[] merged = new VulkanPinnedResourceGeneration[
                firstResources.Length + secondResources.Length];
            firstResources.CopyTo(merged);
            int count = firstResources.Length;
            for (int i = 0; i < secondResources.Length; i++)
            {
                VulkanPinnedResourceGeneration candidate = secondResources[i];
                bool duplicate = false;
                for (int existingIndex = 0; existingIndex < count; existingIndex++)
                {
                    if (merged[existingIndex] == candidate)
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (!duplicate)
                    merged[count++] = candidate;
            }

            if (count != merged.Length)
                Array.Resize(ref merged, count);
            return new VulkanRetirementPinSet(merged);
        }
    }
}
