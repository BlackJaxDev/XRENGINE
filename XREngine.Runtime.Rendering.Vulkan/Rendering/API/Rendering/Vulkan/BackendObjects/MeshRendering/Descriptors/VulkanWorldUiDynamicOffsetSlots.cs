using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan;

/// <summary>Stores bounded native offsets without a draw-time allocation.</summary>
[InlineArray(64)]
internal struct VulkanWorldUiDynamicOffsetSlots
{
    private uint _first;
}
