using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using Format = Silk.NET.Vulkan.Format;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Provides conversions from engine-side <see cref="ESizedInternalFormat"/> values
    /// to Silk.NET Vulkan <see cref="Format"/> values. Used by both image-backed textures
    /// and buffer-view textures.
    /// </summary>
    internal static class VkFormatConversions
    {
        /// <summary>
        /// Converts an engine <see cref="ESizedInternalFormat"/> to the corresponding
        /// Vulkan <see cref="Format"/>. Covers color, SNorm, depth, depth-stencil,
        /// and stencil-only formats.
        /// </summary>
        public static Format FromSizedFormat(ESizedInternalFormat sizedFormat)
            => sizedFormat switch
            {
                // Red
                ESizedInternalFormat.R8 => Format.R8Unorm,
                ESizedInternalFormat.R8Snorm => Format.R8SNorm,
                ESizedInternalFormat.R8i => Format.R8Sint,
                ESizedInternalFormat.R8ui => Format.R8Uint,
                ESizedInternalFormat.R16 => Format.R16Unorm,
                ESizedInternalFormat.R16Snorm => Format.R16SNorm,
                ESizedInternalFormat.R16f => Format.R16Sfloat,
                ESizedInternalFormat.R16i => Format.R16Sint,
                ESizedInternalFormat.R16ui => Format.R16Uint,
                ESizedInternalFormat.R32f => Format.R32Sfloat,
                ESizedInternalFormat.R32i => Format.R32Sint,
                ESizedInternalFormat.R32ui => Format.R32Uint,

                // Red-Green
                ESizedInternalFormat.Rg8 => Format.R8G8Unorm,
                ESizedInternalFormat.Rg8Snorm => Format.R8G8SNorm,
                ESizedInternalFormat.Rg8i => Format.R8G8Sint,
                ESizedInternalFormat.Rg8ui => Format.R8G8Uint,
                ESizedInternalFormat.Rg16 => Format.R16G16Unorm,
                ESizedInternalFormat.Rg16Snorm => Format.R16G16SNorm,
                ESizedInternalFormat.Rg16f => Format.R16G16Sfloat,
                ESizedInternalFormat.Rg16i => Format.R16G16Sint,
                ESizedInternalFormat.Rg16ui => Format.R16G16Uint,
                ESizedInternalFormat.Rg32f => Format.R32G32Sfloat,
                ESizedInternalFormat.Rg32i => Format.R32G32Sint,
                ESizedInternalFormat.Rg32ui => Format.R32G32Uint,

                // RGB — promoted to RGBA equivalents because 3-channel formats
                // (VK_FORMAT_R8G8B8_*) are not supported for images on most desktop GPUs.
                // The pixel upload path must pad 3-channel data to 4 channels to match.
                ESizedInternalFormat.Rgb8 => Format.R8G8B8A8Unorm,
                ESizedInternalFormat.Rgb8Snorm => Format.R8G8B8A8SNorm,
                ESizedInternalFormat.Rgb8i => Format.R8G8B8A8Sint,
                ESizedInternalFormat.Rgb8ui => Format.R8G8B8A8Uint,
                ESizedInternalFormat.Rgb16Snorm => Format.R16G16B16A16SNorm,
                ESizedInternalFormat.Rgb16f => Format.R16G16B16A16Sfloat,
                ESizedInternalFormat.Rgb16i => Format.R16G16B16A16Sint,
                ESizedInternalFormat.Rgb16ui => Format.R16G16B16A16Uint,
                ESizedInternalFormat.Rgb32f => Format.R32G32B32A32Sfloat,
                ESizedInternalFormat.Rgb32i => Format.R32G32B32A32Sint,
                ESizedInternalFormat.Rgb32ui => Format.R32G32B32A32Uint,
                ESizedInternalFormat.Srgb8 => Format.R8G8B8A8Srgb,
                ESizedInternalFormat.R11fG11fB10f => Format.B10G11R11UfloatPack32,
                ESizedInternalFormat.Rgb9E5 => Format.E5B9G9R9UfloatPack32,

                // RGBA
                ESizedInternalFormat.Rgba8 => Format.R8G8B8A8Unorm,
                ESizedInternalFormat.Rgba8Snorm => Format.R8G8B8A8SNorm,
                ESizedInternalFormat.Rgba8i => Format.R8G8B8A8Sint,
                ESizedInternalFormat.Rgba8ui => Format.R8G8B8A8Uint,
                ESizedInternalFormat.Rgba16 => Format.R16G16B16A16Unorm,
                ESizedInternalFormat.Rgba16f => Format.R16G16B16A16Sfloat,
                ESizedInternalFormat.Rgba16i => Format.R16G16B16A16Sint,
                ESizedInternalFormat.Rgba16ui => Format.R16G16B16A16Uint,
                ESizedInternalFormat.Rgba32f => Format.R32G32B32A32Sfloat,
                ESizedInternalFormat.Rgba32i => Format.R32G32B32A32Sint,
                ESizedInternalFormat.Rgba32ui => Format.R32G32B32A32Uint,
                ESizedInternalFormat.Srgb8Alpha8 => Format.R8G8B8A8Srgb,
                ESizedInternalFormat.Rgb10A2 => Format.A2B10G10R10UnormPack32,

                // Packed
                ESizedInternalFormat.R3G3B2 => Format.Undefined, // no direct Vulkan equivalent
                ESizedInternalFormat.Rgb4 => Format.R4G4B4A4UnormPack16, // approximate
                ESizedInternalFormat.Rgb5 => Format.R5G6B5UnormPack16,   // approximate
                ESizedInternalFormat.Rgb5A1 => Format.R5G5B5A1UnormPack16,
                ESizedInternalFormat.Rgb10 => Format.A2B10G10R10UnormPack32, // approximate
                ESizedInternalFormat.Rgb12 => Format.R16G16B16Unorm,        // approximate
                ESizedInternalFormat.Rgba2 => Format.R8G8B8A8Unorm,         // no direct equivalent
                ESizedInternalFormat.Rgba4 => Format.R4G4B4A4UnormPack16,
                ESizedInternalFormat.Rgba12 => Format.R16G16B16A16Unorm,    // approximate

                // Depth
                ESizedInternalFormat.DepthComponent16 => Format.D16Unorm,
                ESizedInternalFormat.DepthComponent24 => Format.X8D24UnormPack32,
                ESizedInternalFormat.DepthComponent32f => Format.D32Sfloat,

                // Depth-Stencil
                ESizedInternalFormat.Depth24Stencil8 => Format.D24UnormS8Uint,
                ESizedInternalFormat.Depth32fStencil8 => Format.D32SfloatS8Uint,

                // Stencil
                ESizedInternalFormat.StencilIndex8 => Format.S8Uint,

                _ => Format.R8G8B8A8Unorm,
            };

        /// <summary>
        /// Converts an engine <see cref="EPixelInternalFormat"/> (typically from mip data)
        /// to a Vulkan <see cref="Format"/>.
        /// </summary>
        public static Format FromPixelInternalFormat(EPixelInternalFormat internalFormat)
            => internalFormat switch
            {
                // Red channel formats
                EPixelInternalFormat.R8 => FromSizedFormat(ESizedInternalFormat.R8),
                EPixelInternalFormat.R8SNorm => FromSizedFormat(ESizedInternalFormat.R8Snorm),
                EPixelInternalFormat.R16 => FromSizedFormat(ESizedInternalFormat.R16),
                EPixelInternalFormat.R16SNorm => FromSizedFormat(ESizedInternalFormat.R16Snorm),
                EPixelInternalFormat.R16f => FromSizedFormat(ESizedInternalFormat.R16f),
                EPixelInternalFormat.R32f => FromSizedFormat(ESizedInternalFormat.R32f),
                EPixelInternalFormat.R8i => FromSizedFormat(ESizedInternalFormat.R8i),
                EPixelInternalFormat.R8ui => FromSizedFormat(ESizedInternalFormat.R8ui),
                EPixelInternalFormat.R16i => FromSizedFormat(ESizedInternalFormat.R16i),
                EPixelInternalFormat.R16ui => FromSizedFormat(ESizedInternalFormat.R16ui),
                EPixelInternalFormat.R32i => FromSizedFormat(ESizedInternalFormat.R32i),
                EPixelInternalFormat.R32ui => FromSizedFormat(ESizedInternalFormat.R32ui),

                // RG channel formats
                EPixelInternalFormat.RG8 => FromSizedFormat(ESizedInternalFormat.Rg8),
                EPixelInternalFormat.RG8SNorm => FromSizedFormat(ESizedInternalFormat.Rg8Snorm),
                EPixelInternalFormat.RG16 => FromSizedFormat(ESizedInternalFormat.Rg16),
                EPixelInternalFormat.RG16SNorm => FromSizedFormat(ESizedInternalFormat.Rg16Snorm),
                EPixelInternalFormat.RG16f => FromSizedFormat(ESizedInternalFormat.Rg16f),
                EPixelInternalFormat.RG32f => FromSizedFormat(ESizedInternalFormat.Rg32f),
                EPixelInternalFormat.RG8i => FromSizedFormat(ESizedInternalFormat.Rg8i),
                EPixelInternalFormat.RG8ui => FromSizedFormat(ESizedInternalFormat.Rg8ui),
                EPixelInternalFormat.RG16i => FromSizedFormat(ESizedInternalFormat.Rg16i),
                EPixelInternalFormat.RG16ui => FromSizedFormat(ESizedInternalFormat.Rg16ui),
                EPixelInternalFormat.RG32i => FromSizedFormat(ESizedInternalFormat.Rg32i),
                EPixelInternalFormat.RG32ui => FromSizedFormat(ESizedInternalFormat.Rg32ui),

                // RGB formats
                EPixelInternalFormat.R3G3B2 => FromSizedFormat(ESizedInternalFormat.R3G3B2),
                EPixelInternalFormat.Rgb4 => FromSizedFormat(ESizedInternalFormat.Rgb4),
                EPixelInternalFormat.Rgb5 => FromSizedFormat(ESizedInternalFormat.Rgb5),
                EPixelInternalFormat.Rgb8 => FromSizedFormat(ESizedInternalFormat.Rgb8),
                EPixelInternalFormat.Rgb8SNorm => FromSizedFormat(ESizedInternalFormat.Rgb8Snorm),
                EPixelInternalFormat.Rgb10 => FromSizedFormat(ESizedInternalFormat.Rgb10),
                EPixelInternalFormat.Rgb12 => FromSizedFormat(ESizedInternalFormat.Rgb12),
                EPixelInternalFormat.Rgb16SNorm => FromSizedFormat(ESizedInternalFormat.Rgb16Snorm),
                EPixelInternalFormat.Srgb8 => FromSizedFormat(ESizedInternalFormat.Srgb8),
                EPixelInternalFormat.Rgb16f => FromSizedFormat(ESizedInternalFormat.Rgb16f),
                EPixelInternalFormat.Rgb32f => FromSizedFormat(ESizedInternalFormat.Rgb32f),
                EPixelInternalFormat.R11fG11fB10f => FromSizedFormat(ESizedInternalFormat.R11fG11fB10f),
                EPixelInternalFormat.Rgb9E5 => FromSizedFormat(ESizedInternalFormat.Rgb9E5),
                EPixelInternalFormat.Rgb8i => FromSizedFormat(ESizedInternalFormat.Rgb8i),
                EPixelInternalFormat.Rgb8ui => FromSizedFormat(ESizedInternalFormat.Rgb8ui),
                EPixelInternalFormat.Rgb16i => FromSizedFormat(ESizedInternalFormat.Rgb16i),
                EPixelInternalFormat.Rgb16ui => FromSizedFormat(ESizedInternalFormat.Rgb16ui),
                EPixelInternalFormat.Rgb32i => FromSizedFormat(ESizedInternalFormat.Rgb32i),
                EPixelInternalFormat.Rgb32ui => FromSizedFormat(ESizedInternalFormat.Rgb32ui),

                // RGBA formats
                EPixelInternalFormat.Rgba2 => FromSizedFormat(ESizedInternalFormat.Rgba2),
                EPixelInternalFormat.Rgba4 => FromSizedFormat(ESizedInternalFormat.Rgba4),
                EPixelInternalFormat.Rgb5A1 => FromSizedFormat(ESizedInternalFormat.Rgb5A1),
                EPixelInternalFormat.Rgba8 => FromSizedFormat(ESizedInternalFormat.Rgba8),
                EPixelInternalFormat.Rgba8SNorm => FromSizedFormat(ESizedInternalFormat.Rgba8Snorm),
                EPixelInternalFormat.Rgb10A2 => FromSizedFormat(ESizedInternalFormat.Rgb10A2),
                EPixelInternalFormat.Rgba12 => FromSizedFormat(ESizedInternalFormat.Rgba12),
                EPixelInternalFormat.Rgba16 => FromSizedFormat(ESizedInternalFormat.Rgba16),
                EPixelInternalFormat.Srgb8Alpha8 => FromSizedFormat(ESizedInternalFormat.Srgb8Alpha8),
                EPixelInternalFormat.Rgba16f => FromSizedFormat(ESizedInternalFormat.Rgba16f),
                EPixelInternalFormat.Rgba32f => FromSizedFormat(ESizedInternalFormat.Rgba32f),
                EPixelInternalFormat.Rgba8i => FromSizedFormat(ESizedInternalFormat.Rgba8i),
                EPixelInternalFormat.Rgba8ui => FromSizedFormat(ESizedInternalFormat.Rgba8ui),
                EPixelInternalFormat.Rgba16i => FromSizedFormat(ESizedInternalFormat.Rgba16i),
                EPixelInternalFormat.Rgba16ui => FromSizedFormat(ESizedInternalFormat.Rgba16ui),
                EPixelInternalFormat.Rgba32i => FromSizedFormat(ESizedInternalFormat.Rgba32i),
                EPixelInternalFormat.Rgba32ui => FromSizedFormat(ESizedInternalFormat.Rgba32ui),

                // Depth formats
                EPixelInternalFormat.DepthComponent16 => FromSizedFormat(ESizedInternalFormat.DepthComponent16),
                EPixelInternalFormat.DepthComponent24 => FromSizedFormat(ESizedInternalFormat.DepthComponent24),
                EPixelInternalFormat.DepthComponent32f => FromSizedFormat(ESizedInternalFormat.DepthComponent32f),

                // Depth-stencil formats
                EPixelInternalFormat.Depth24Stencil8 => FromSizedFormat(ESizedInternalFormat.Depth24Stencil8),
                EPixelInternalFormat.Depth32fStencil8 => FromSizedFormat(ESizedInternalFormat.Depth32fStencil8),

                // Stencil formats
                EPixelInternalFormat.StencilIndex8 => FromSizedFormat(ESizedInternalFormat.StencilIndex8),

                _ => Format.Undefined,
            };

        /// <summary>
        /// Returns <c>true</c> if the given <see cref="Format"/> is a depth or depth-stencil format.
        /// </summary>
        public static bool IsDepthStencilFormat(Format format)
            => format is Format.D16Unorm
                or Format.X8D24UnormPack32
                or Format.D32Sfloat
                or Format.D16UnormS8Uint
                or Format.D24UnormS8Uint
                or Format.D32SfloatS8Uint
                or Format.S8Uint;
    }
}
