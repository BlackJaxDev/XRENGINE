using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns the device-lifetime state of the global bindless material texture table.
/// </summary>
internal sealed class VulkanBindlessMaterialTextureTableState
{
    internal readonly object Sync = new();
    internal readonly Dictionary<XRTexture, uint> SlotsByTexture =
        new(ReferenceTextureComparer.Instance);
    internal readonly Queue<uint> FreeSlots = new();
    internal readonly VulkanBindlessDescriptorPublicationStream PublicationStream = new();
    internal MaterialTextureDescriptorSlot[] Slots = [];
    internal DescriptorSetLayout SetLayout;
    internal DescriptorPool Pool;
    internal DescriptorSet Set;
    internal uint Capacity;
    internal uint NextSlot = 1u;
    internal bool UsesUpdateAfterBind;
    internal bool UsesVariableDescriptorCount;
    internal VkRenderProgram? ScopeProgram;
    internal string ScopeConsumer = string.Empty;
    internal ulong WritesTotal;
    internal ulong WritesLastFlush;
    internal ulong SlotRetirementsTotal;
    internal ulong FallbackReferencesTotal;
    internal VulkanBindlessMaterialCapability Capability;
}
