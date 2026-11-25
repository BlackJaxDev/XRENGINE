using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Identifies the descriptor allocation and native sets used by one draw.</summary>
internal struct VulkanWorldUiDescriptorBindingWitness
{
    internal VkMeshRenderer.DescriptorAllocation? Allocation;
    internal VkRenderProgram? Program;
    internal DescriptorSet[][]? Sets;
    internal int Slot;
    internal ulong ContentGeneration;
    internal ulong TopologyGeneration;
    internal ulong InterfaceGeneration;
    internal ulong LayoutHandle;
    internal ulong MaterialResourceVersion;
    internal uint ActiveMask;
    internal int SetCount;
    internal VulkanWorldUiDescriptorSetWitnessSlots SetWitnesses;
    internal VulkanWorldUiDynamicOffsetSlots DynamicOffsets;
    internal int DynamicOffsetCount;
    internal bool Complete;
}
