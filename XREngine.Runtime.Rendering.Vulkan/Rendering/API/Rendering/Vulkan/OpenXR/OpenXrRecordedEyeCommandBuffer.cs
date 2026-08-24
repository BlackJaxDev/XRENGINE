using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct OpenXrRecordedEyeCommandBuffer(
    CommandBuffer CommandBuffer,
    VulkanOpenXrFrameContext FrameContext,
    uint OpenXrViewIndex,
    uint OpenXrImageIndex,
    uint FrameDataSlotIndex,
    ulong FrameOpsSignature,
    ulong PlannerRevision,
    ulong FrameOpContextId,
    ulong ResourceGeneration,
    ulong DescriptorGeneration,
    bool OwnedByOpenXrPrimaryCache);
