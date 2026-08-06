namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns the bounded per-frame retirement queues and their deduplication state.
/// </summary>
/// <remarks>
/// Queue storage is allocated once with the renderer and reused for every
/// frame. Readiness policy and Vulkan destruction remain in the renderer
/// migration facade until they can move with their device/API dependencies.
/// </remarks>
internal sealed class VulkanResourceRetirementQueue
{
    internal VulkanResourceRetirementQueue(int frameSlotCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameSlotCount);

        Buffers = CreateLists<RetiredBuffer>(frameSlotCount);
        BufferHandles = CreateSets<ulong>(frameSlotCount);
        MemoryHandles = CreateSets<ulong>(frameSlotCount);
        Framebuffers = CreateLists<RetiredFramebuffer>(frameSlotCount);
        FramebufferHandles = CreateSets<ulong>(frameSlotCount);
        DescriptorPools = CreateLists<RetiredDescriptorPool>(frameSlotCount);
        DescriptorPoolHandles = CreateSets<ulong>(frameSlotCount);
        DescriptorSets = CreateLists<RetiredDescriptorSet>(frameSlotCount);
        DescriptorSetHandles = CreateSets<ulong>(frameSlotCount);
        Pipelines = CreateLists<RetiredPipeline>(frameSlotCount);
        PipelineHandles = CreateSets<ulong>(frameSlotCount);
        PipelineLayouts = CreateLists<VulkanRenderer.RetiredPipelineLayout>(frameSlotCount);
        PipelineLayoutHandles = CreateSets<ulong>(frameSlotCount);
        DescriptorSetLayouts = CreateLists<VulkanRenderer.RetiredDescriptorSetLayout>(frameSlotCount);
        DescriptorSetLayoutHandles = CreateSets<ulong>(frameSlotCount);
        QueryPools = CreateLists<RetiredQueryPool>(frameSlotCount);
        QueryPoolHandles = CreateSets<ulong>(frameSlotCount);
        CommandBuffers = CreateLists<RetiredCommandBuffer>(frameSlotCount);
        CommandBufferHandles = CreateSets<ulong>(frameSlotCount);
        CommandPools = CreateLists<RetiredCommandPool>(frameSlotCount);
        CommandPoolHandles = CreateSets<ulong>(frameSlotCount);
        BufferViews = CreateLists<RetiredBufferView>(frameSlotCount);
        BufferViewHandles = CreateSets<ulong>(frameSlotCount);
        Images = CreateLists<RetiredImageResourceEntry>(frameSlotCount);
        ImageHandles = CreateSets<ulong>(frameSlotCount);
        ImageMemoryHandles = CreateSets<ulong>(frameSlotCount);
        ImageViewHandles = CreateSets<VulkanPinnedResourceGeneration>(frameSlotCount);
        SamplerHandles = CreateSets<ulong>(frameSlotCount);
    }

    internal object SyncRoot { get; } = new();

    internal List<RetiredBuffer>[] Buffers { get; }
    internal HashSet<ulong>[] BufferHandles { get; }
    internal HashSet<ulong> AllBufferHandles { get; } = [];
    internal HashSet<ulong>[] MemoryHandles { get; }
    internal HashSet<ulong> AllMemoryHandles { get; } = [];

    internal List<RetiredFramebuffer>[] Framebuffers { get; }
    internal HashSet<ulong>[] FramebufferHandles { get; }
    internal HashSet<ulong> AllFramebufferHandles { get; } = [];

    internal List<RetiredDescriptorPool>[] DescriptorPools { get; }
    internal HashSet<ulong>[] DescriptorPoolHandles { get; }
    internal HashSet<ulong> AllDescriptorPoolHandles { get; } = [];

    internal List<RetiredDescriptorSet>[] DescriptorSets { get; }
    internal HashSet<ulong>[] DescriptorSetHandles { get; }
    internal HashSet<ulong> AllDescriptorSetHandles { get; } = [];

    internal List<RetiredPipeline>[] Pipelines { get; }
    internal HashSet<ulong>[] PipelineHandles { get; }
    internal HashSet<ulong> AllPipelineHandles { get; } = [];
    internal List<VulkanRenderer.RetiredPipelineLayout>[] PipelineLayouts { get; }
    internal HashSet<ulong>[] PipelineLayoutHandles { get; }
    internal HashSet<ulong> AllPipelineLayoutHandles { get; } = [];
    internal List<VulkanRenderer.RetiredDescriptorSetLayout>[] DescriptorSetLayouts { get; }
    internal HashSet<ulong>[] DescriptorSetLayoutHandles { get; }
    internal HashSet<ulong> AllDescriptorSetLayoutHandles { get; } = [];

    internal List<RetiredQueryPool>[] QueryPools { get; }
    internal HashSet<ulong>[] QueryPoolHandles { get; }
    internal HashSet<ulong> AllQueryPoolHandles { get; } = [];

    internal List<RetiredCommandBuffer>[] CommandBuffers { get; }
    internal HashSet<ulong>[] CommandBufferHandles { get; }
    internal HashSet<ulong> AllCommandBufferHandles { get; } = [];

    internal List<RetiredCommandPool>[] CommandPools { get; }
    internal HashSet<ulong>[] CommandPoolHandles { get; }
    internal HashSet<ulong> AllCommandPoolHandles { get; } = [];

    internal List<RetiredBufferView>[] BufferViews { get; }
    internal HashSet<ulong>[] BufferViewHandles { get; }
    internal HashSet<ulong> AllBufferViewHandles { get; } = [];

    internal List<RetiredImageResourceEntry>[] Images { get; }
    internal HashSet<ulong>[] ImageHandles { get; }
    internal HashSet<ulong> AllImageHandles { get; } = [];
    internal HashSet<ulong>[] ImageMemoryHandles { get; }
    internal HashSet<ulong> AllImageMemoryHandles { get; } = [];
    internal HashSet<VulkanPinnedResourceGeneration>[] ImageViewHandles { get; }
    internal HashSet<VulkanPinnedResourceGeneration> AllImageViewHandles { get; } = [];
    internal HashSet<ulong>[] SamplerHandles { get; }
    internal HashSet<ulong> AllSamplerHandles { get; } = [];

    /// <summary>
    /// Atomically reserves a native handle and appends its retirement entry.
    /// Callers must hold <see cref="SyncRoot"/>.
    /// </summary>
    internal static bool TryEnqueueUniqueNoLock<TEntry, THandle>(
        int frameSlot,
        THandle handle,
        TEntry entry,
        List<TEntry>[] entries,
        HashSet<THandle>[] slotHandles,
        HashSet<THandle> allHandles)
        where THandle : notnull
    {
        if (!allHandles.Add(handle))
            return false;

        slotHandles[frameSlot].Add(handle);
        entries[frameSlot].Add(entry);
        return true;
    }

    /// <summary>
    /// Releases the deduplication reservation after an entry leaves its queue.
    /// Callers must hold <see cref="SyncRoot"/>.
    /// </summary>
    internal static void ReleaseUniqueNoLock<THandle>(
        int frameSlot,
        THandle handle,
        HashSet<THandle>[] slotHandles,
        HashSet<THandle> allHandles)
        where THandle : notnull
    {
        slotHandles[frameSlot].Remove(handle);
        allHandles.Remove(handle);
    }

    private static List<T>[] CreateLists<T>(int count)
    {
        List<T>[] result = new List<T>[count];
        for (int i = 0; i < result.Length; i++)
            result[i] = [];
        return result;
    }

    private static HashSet<T>[] CreateSets<T>(int count)
    {
        HashSet<T>[] result = new HashSet<T>[count];
        for (int i = 0; i < result.Length; i++)
            result[i] = [];
        return result;
    }
}
