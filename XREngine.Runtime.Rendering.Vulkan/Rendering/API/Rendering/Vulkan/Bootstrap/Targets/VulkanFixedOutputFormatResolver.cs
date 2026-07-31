using Silk.NET.Vulkan;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>Maps fixed renderer-target formats to their Vulkan representation.</summary>
internal static class VulkanFixedOutputFormatResolver
{
    public static Format ResolveColorFormat(EPixelInternalFormat format)
        => format switch
        {
            EPixelInternalFormat.Rgba8 => Format.R8G8B8A8Unorm,
            EPixelInternalFormat.Rgba16f => Format.R16G16B16A16Sfloat,
            EPixelInternalFormat.Rgba32f => Format.R32G32B32A32Sfloat,
            _ => throw new NotSupportedException($"Vulkan fixed-output color format '{format}' is unsupported."),
        };

    public static Format ResolveDepthFormat(EPixelInternalFormat format)
        => format switch
        {
            EPixelInternalFormat.Depth24Stencil8 => Format.D24UnormS8Uint,
            EPixelInternalFormat.Depth32fStencil8 => Format.D32SfloatS8Uint,
            EPixelInternalFormat.DepthComponent32f => Format.D32Sfloat,
            _ => throw new NotSupportedException($"Vulkan fixed-output depth format '{format}' is unsupported."),
        };

    public static ulong BytesPerPixel(EPixelInternalFormat format)
        => format switch
        {
            EPixelInternalFormat.Rgba8 => 4,
            EPixelInternalFormat.Rgba16f => 8,
            EPixelInternalFormat.Rgba32f => 16,
            _ => throw new NotSupportedException($"Vulkan fixed-output readback does not support color format '{format}'."),
        };

    public static ImageAspectFlags DepthAspect(Format format)
        => format == Format.D32Sfloat
            ? ImageAspectFlags.DepthBit
            : ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit;
}
