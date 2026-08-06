using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct OpenXrEyeSwapchainRenderRequest(
    Image Image,
    Format Format,
    Extent2D Extent,
    int ResourcePlannerStateIndex,
    uint OpenXrViewIndex,
    uint OpenXrImageIndex,
    ViewFoveationContext Foveation,
    Action EmitFrameOps,
    ulong ViewBatchStructuralIdentity = 0UL);
