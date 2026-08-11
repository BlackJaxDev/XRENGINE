using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Current-frame producer payload and frozen authority used to prove that an
/// already recorded desktop primary can be refreshed without sealing a new
/// <see cref="FramePlan"/>.
/// </summary>
internal readonly record struct VulkanPreparedPrimaryReuseInput(
    uint ImageIndex,
    CommandBuffer PrimaryCommandBuffer,
    FrameOp[] StaticOperations,
    FrameOp[] DynamicUiOperations,
    ulong StaticOperationSignature,
    ulong DynamicUiOperationSignature,
    ulong RenderFrameId,
    CommandChainSchedule CommandChainSchedule,
    VulkanPreparedPrimaryAuthority Authority);
