using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan;

/// <summary>Stores a bounded set witness without a draw-time allocation.</summary>
[InlineArray(16)]
internal struct VulkanWorldUiDescriptorSetWitnessSlots
{
    private VulkanWorldUiDescriptorSetWitness _first;
}
