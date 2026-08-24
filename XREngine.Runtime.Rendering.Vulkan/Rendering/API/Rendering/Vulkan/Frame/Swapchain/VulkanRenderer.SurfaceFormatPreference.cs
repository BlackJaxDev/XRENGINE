using Silk.NET.Vulkan;
using Format = Silk.NET.Vulkan.Format;

namespace XREngine.Rendering.Vulkan;

internal readonly struct SurfaceFormatPreference(Format format, ColorSpaceKHR colorSpace)
{
    public Format Format { get; } = format;
    public ColorSpaceKHR ColorSpace { get; } = colorSpace;
}
