using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable producer-side description of a mesh operation.  It deliberately
/// carries captured facts and wrapper identities only: frame-loop sequencing,
/// planning, native command recording, output observation, and telemetry stay
/// with their respective authorities.
/// </summary>
internal readonly record struct VulkanMeshOperationRequest(
    VkMeshRenderer Renderer,
    int PassIndex,
    PendingMeshDraw Draw,
    VulkanMeshProducerSnapshot ProducerSnapshot,
    XRFrameBuffer? ExplicitTarget,
    bool RequiresExternalUploadBlock)
{
    internal FrameOpContext Context
        => ProducerSnapshot.Context;

    internal CommandBuffer? CommandBuffer { get; init; }
}
