using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal struct SwapChainSupportDetails
{
    public SurfaceCapabilitiesKHR Capabilities;
    public SurfaceFormatKHR[] Formats;
    public PresentModeKHR[] PresentModes;
}
