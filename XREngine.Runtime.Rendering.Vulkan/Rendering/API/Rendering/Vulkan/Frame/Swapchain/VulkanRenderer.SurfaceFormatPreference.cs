using Silk.NET.Vulkan;
using Format = Silk.NET.Vulkan.Format;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly struct SurfaceFormatPreference(Format format, ColorSpaceKHR colorSpace)
    {
        public Format Format { get; } = format;
        public ColorSpaceKHR ColorSpace { get; } = colorSpace;
    }
}
