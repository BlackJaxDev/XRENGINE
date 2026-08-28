using Silk.NET.Vulkan;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Vulkan;

/// <summary>Preallocated mutable state for one in-flight Vulkan frame slot.</summary>
internal sealed class VulkanAdvancedSceneResourceSlot
{
    internal VulkanAdvancedSceneResourceSlot(
        int publicationCapacity,
        int receiptCapacity)
    {
        Entries = new VulkanAdvancedScenePublicationEntry[publicationCapacity];
        GlobalDescriptorSets = new DescriptorSet[publicationCapacity];
        ReceiptStates = new VulkanAdvancedScenePublicationUseState[receiptCapacity];
        for (int index = 0; index < ReceiptStates.Length; ++index)
            ReceiptStates[index] = new VulkanAdvancedScenePublicationUseState();
    }

    internal VulkanAdvancedScenePublicationEntry[] Entries { get; }
    internal DescriptorSet[] GlobalDescriptorSets { get; }
    internal VulkanAdvancedScenePublicationUseState[] ReceiptStates { get; }
    internal DescriptorSet ResourceDescriptorSet;
    internal ulong FrameGeneration;
    internal uint NextTextureDescriptor = 1u;
    internal uint NextSamplerDescriptor = 1u;
    internal int EntryCount;
    internal int ReceiptCount;
    internal int ActiveUseCount;
    internal ulong StorageBytesConsumed;
    internal bool Quarantined;

    internal int Find(
        AdvancedSharedGpuSceneDatabase database,
        in AdvancedGpuScenePublicationReference publication)
    {
        for (int index = 0; index < EntryCount; ++index)
            if (ReferenceEquals(Entries[index].Database, database) &&
                Entries[index].Publication == publication)
            {
                return index;
            }

        return -1;
    }

    internal void BeginGeneration(ulong generation)
    {
        if (ActiveUseCount != 0)
            throw new InvalidOperationException(
                "A Vulkan advanced-scene frame slot cannot be recycled while native publication uses remain active.");

        for (int index = 0; index < EntryCount; ++index)
            Entries[index].Clear();

        FrameGeneration = generation;
        NextTextureDescriptor = 1u;
        NextSamplerDescriptor = 1u;
        EntryCount = 0;
        ReceiptCount = 0;
        StorageBytesConsumed = 0u;
        Quarantined = false;
    }
}
