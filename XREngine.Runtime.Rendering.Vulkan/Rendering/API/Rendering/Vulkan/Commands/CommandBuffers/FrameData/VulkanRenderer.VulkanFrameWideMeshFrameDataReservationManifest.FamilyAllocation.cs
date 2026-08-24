using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameWideMeshFrameDataReservationManifest
{
    private readonly record struct FamilyAllocation(int BaseSlot, int SlotCount);
}

