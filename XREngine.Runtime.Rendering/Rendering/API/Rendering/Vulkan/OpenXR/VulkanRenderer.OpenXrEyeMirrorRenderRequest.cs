using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal readonly record struct OpenXrEyeMirrorRenderRequest(
        XRFrameBuffer TargetFrameBuffer,
        Extent2D Extent,
        int ResourcePlannerStateIndex,
        uint OpenXrViewIndex,
        uint OpenXrImageIndex,
        Action EmitFrameOps,
        bool RendersExternalSwapchainTarget = true,
        ulong ViewBatchStructuralIdentity = 0UL);
}
