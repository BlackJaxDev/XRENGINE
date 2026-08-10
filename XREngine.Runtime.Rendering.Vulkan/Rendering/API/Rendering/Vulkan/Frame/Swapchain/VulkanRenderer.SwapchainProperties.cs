using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Image = Silk.NET.Vulkan.Image;
using Format = Silk.NET.Vulkan.Format;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    public Format PreferredFormat
    {
        get => OutputRuntime.Desktop.PreferredFormat;
        set => OutputRuntime.Desktop.PreferredFormat = value;
    }

    public ColorSpaceKHR PreferredColorSpace
    {
        get => OutputRuntime.Desktop.PreferredColorSpace;
        set => OutputRuntime.Desktop.PreferredColorSpace = value;
    }

    public PresentModeKHR PreferredPresentMode
    {
        get => OutputRuntime.Desktop.PreferredPresentMode;
        set => OutputRuntime.Desktop.PreferredPresentMode = value;
    }

    public PresentModeKHR FallbackPresentMode
    {
        get => OutputRuntime.Desktop.FallbackPresentMode;
        set => OutputRuntime.Desktop.FallbackPresentMode = value;
    }

    private Image _swapchainDepthImage => OutputRuntime.DesktopDepthImage;
    private ImageView _swapchainDepthView => OutputRuntime.DesktopDepthView;
    private Format _swapchainDepthFormat => OutputRuntime.DesktopDepthFormat;
    private ImageAspectFlags _swapchainDepthAspect => OutputRuntime.DesktopDepthAspect;
    internal bool StreamlineFrameGenerationSwapchainActive => OutputRuntime.Desktop.StreamlineFrameGenerationActive;
    internal bool StreamlineFrameGenerationSwapchainIncludesDlss => OutputRuntime.Desktop.StreamlineFrameGenerationIncludesDlss;
    internal uint SwapchainImageCount => (uint)(OutputRuntime.Desktop.Images?.Length ?? 0);
    internal Format SwapchainImageFormat => OutputRuntime.Desktop.ImageFormat;
    internal Extent2D SwapchainExtent => OutputRuntime.Desktop.Extent;
}
