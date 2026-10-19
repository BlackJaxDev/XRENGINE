namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameWideMeshFrameDataReservationManifest
{
    private sealed class FamilyAllocation(
        int baseSlot,
        int slotCount,
        VkMeshRenderer renderer,
        int frameDataSlot,
        RenderCaptureOwnerToken? captureOwner,
        VulkanMappedFrameArena? arena,
        VulkanMappedFrameAllocation? guard)
    {
        internal int BaseSlot { get; set; } = baseSlot;
        internal int SlotCount { get; set; } = slotCount;
        internal VkMeshRenderer Renderer { get; } = renderer;
        internal int FrameDataSlot { get; } = frameDataSlot;
        internal RenderCaptureOwnerToken? CaptureOwner { get; } = captureOwner;
        internal VulkanMappedFrameArena? Arena { get; } = arena;
        internal VulkanMappedFrameAllocation? Guard { get; } = guard;
    }

    private readonly record struct FamilyHole(
        VkMeshRenderer Renderer,
        int FrameDataSlot,
        int BaseSlot,
        int SlotCount);
}

