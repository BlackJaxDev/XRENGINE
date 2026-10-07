using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Shares immutable program interfaces within one logical-device generation.</summary>
internal unsafe sealed class VulkanProgramInterfaceCache(VulkanResourceRuntime resources)
{
    private readonly object _sync = new();
    private readonly Dictionary<int, List<VulkanProgramInterfaceEntry>> _buckets = [];
    private long _nextGeneration;

    internal VulkanProgramInterfaceEntry Acquire(
        VulkanProgramInterfaceKey key,
        VkRenderProgram program,
        bool cacheable)
    {
        lock (_sync)
        {
            if (cacheable && _buckets.TryGetValue(key.GetHashCode(), out List<VulkanProgramInterfaceEntry>? bucket))
            {
                foreach (VulkanProgramInterfaceEntry candidate in bucket)
                {
                    if (!candidate.Key.Equals(key))
                        continue;
                    checked { ++candidate.RetainCount; }
                    return candidate;
                }
            }

            ulong generation = unchecked((ulong)checked(++_nextGeneration));
            VulkanProgramInterfaceEntry entry = program.CreateProgramInterfaceEntry(key, generation);
            entry.RetainCount = 1;
            entry.Cached = cacheable;
            if (cacheable)
            {
                if (!_buckets.TryGetValue(key.GetHashCode(), out bucket))
                    _buckets.Add(key.GetHashCode(), bucket = []);
                bucket.Add(entry);
            }
            return entry;
        }
    }

    internal bool TryRetain(VulkanProgramInterfaceEntry entry)
    {
        lock (_sync)
        {
            if (entry.RetainCount <= 0)
                return false;
            checked { ++entry.RetainCount; }
            return true;
        }
    }

    internal void Release(VulkanProgramInterfaceEntry entry)
    {
        lock (_sync)
        {
            if (entry.RetainCount <= 0)
                throw new InvalidOperationException("The Vulkan interface entry was released twice.");
            if (--entry.RetainCount != 0)
                return;
            if (entry.Cached && _buckets.TryGetValue(entry.Key.GetHashCode(), out List<VulkanProgramInterfaceEntry>? bucket))
            {
                bucket.Remove(entry);
                if (bucket.Count == 0)
                    _buckets.Remove(entry.Key.GetHashCode());
            }
        }

        DescriptorLayoutBuildResult descriptors = entry.Descriptors;
        for (int setIndex = 0; setIndex < descriptors.Layouts.Length; ++setIndex)
        {
            if (!VulkanAdvancedSceneProgramBindingContract.IsExternallyOwnedSet(
                    descriptors.ExternallyOwnedSetMask, (uint)setIndex))
                resources.Descriptors.ReleaseProgramDescriptorSetLayout(descriptors.Layouts[setIndex]);
        }

        PipelineLayout pipelineLayout = entry.PipelineLayout;
        if (pipelineLayout.Handle != 0 &&
            resources.TryBeginDestroyPipelineLayout(pipelineLayout, "VulkanProgramInterfaceCache.Release"))
            entry.Context.Api.DestroyPipelineLayout(entry.Context.Device, pipelineLayout, null);
    }
}
