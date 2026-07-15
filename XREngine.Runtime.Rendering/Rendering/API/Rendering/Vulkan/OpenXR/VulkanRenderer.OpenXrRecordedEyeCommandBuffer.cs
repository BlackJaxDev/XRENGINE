using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly record struct OpenXrRecordedEyeCommandBuffer(
        CommandBuffer CommandBuffer,
        uint OpenXrViewIndex,
        uint OpenXrImageIndex,
        uint FrameDataSlotIndex,
        ulong FrameOpsSignature,
        ulong PlannerRevision,
        ulong FrameOpContextId,
        ulong ResourceGeneration,
        ulong DescriptorGeneration,
        bool OwnedByOpenXrPrimaryCache);
}
