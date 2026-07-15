using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal readonly record struct OpenXrEyeSwapchainRenderRequest(
        Image Image,
        Format Format,
        Extent2D Extent,
        int ResourcePlannerStateIndex,
        uint OpenXrViewIndex,
        uint OpenXrImageIndex,
        ViewFoveationContext Foveation,
        Action EmitFrameOps);
}
