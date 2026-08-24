using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable producer-side state captured before a mesh draw enters the frame-operation queue.
/// Mesh wrappers consume this value instead of retaining the renderer facade or consulting live
/// output/planner state while constructing a draw payload.
/// </summary>
internal readonly record struct VulkanMeshProducerSnapshot(
    FrameOpContext Context,
    XRFrameBuffer? Target,
    Extent2D TargetExtent,
    Viewport Viewport,
    Rect2D Scissor,
    IndexedViewportScissorSnapshot IndexedViewportScissors,
    VulkanFixedFunctionStateSnapshot FixedFunctionState,
    bool IsExternalSwapchainTarget,
    bool IsPrewarmingExternalSwapchainTarget)
{
    private readonly FrameOpContext _context = Context;

    public FrameOpContext Context
    {
        get => _context;
        init => _context = value;
    }

    internal static ref readonly FrameOpContext GetContextReference(
        in VulkanMeshProducerSnapshot snapshot)
        => ref snapshot._context;
}
