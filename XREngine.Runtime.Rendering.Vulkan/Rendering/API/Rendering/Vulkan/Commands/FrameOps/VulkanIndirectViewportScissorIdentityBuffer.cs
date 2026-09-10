using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan;

/// <summary>Inline command-state storage; publication never shares mutable arrays.</summary>
[InlineArray(Capacity)]
internal struct VulkanIndirectViewportScissorIdentityBuffer
{
    internal const int Capacity = 16;
    private VulkanIndirectViewportScissorIdentity _first;
}
