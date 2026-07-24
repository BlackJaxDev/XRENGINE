using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public partial class VulkanRenderer
{
    bool IStreamlinePresentationBackendCapability.StreamlineFrameGenerationSwapchainActive
        => StreamlineFrameGenerationSwapchainActive;

    bool IStreamlinePresentationBackendCapability.SwapchainRequiresSrgbEncoding
        => SwapchainImageFormat is Format.B8G8R8A8Unorm or Format.R8G8B8A8Unorm;
}
