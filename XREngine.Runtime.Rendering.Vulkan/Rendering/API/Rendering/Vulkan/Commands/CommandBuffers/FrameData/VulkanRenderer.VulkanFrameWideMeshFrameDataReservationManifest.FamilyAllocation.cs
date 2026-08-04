using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal sealed partial class VulkanFrameWideMeshFrameDataReservationManifest
    {
        private readonly record struct FamilyAllocation(int BaseSlot, int SlotCount);
    }
}

