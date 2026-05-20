using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine
{
    public static partial class Engine
    {
        public static partial class Rendering
        {
            public static partial class Stats
            {
                public static class PixelFormats
                {
                    /// <summary>
                    /// Gets the bytes per pixel for a given sized internal format.
                    /// </summary>
                    public static int GetBytesPerPixel(ESizedInternalFormat format)
                    {
                        return format switch
                        {
                            // 1-byte formats
                            ESizedInternalFormat.R8 => 1,
                            ESizedInternalFormat.R8Snorm => 1,
                            ESizedInternalFormat.R8i => 1,
                            ESizedInternalFormat.R8ui => 1,
                            ESizedInternalFormat.StencilIndex8 => 1,

                            // 2-byte formats
                            ESizedInternalFormat.R16 => 2,
                            ESizedInternalFormat.R16Snorm => 2,
                            ESizedInternalFormat.R16f => 2,
                            ESizedInternalFormat.R16i => 2,
                            ESizedInternalFormat.R16ui => 2,
                            ESizedInternalFormat.Rg8 => 2,
                            ESizedInternalFormat.Rg8Snorm => 2,
                            ESizedInternalFormat.Rg8i => 2,
                            ESizedInternalFormat.Rg8ui => 2,
                            ESizedInternalFormat.DepthComponent16 => 2,

                            // 3-byte formats
                            ESizedInternalFormat.Rgb8 => 3,
                            ESizedInternalFormat.Rgb8Snorm => 3,
                            ESizedInternalFormat.Srgb8 => 3,
                            ESizedInternalFormat.Rgb8i => 3,
                            ESizedInternalFormat.Rgb8ui => 3,
                            ESizedInternalFormat.DepthComponent24 => 3,

                            // 4-byte formats
                            ESizedInternalFormat.R32f => 4,
                            ESizedInternalFormat.R32i => 4,
                            ESizedInternalFormat.R32ui => 4,
                            ESizedInternalFormat.Rg16 => 4,
                            ESizedInternalFormat.Rg16Snorm => 4,
                            ESizedInternalFormat.Rg16f => 4,
                            ESizedInternalFormat.Rg16i => 4,
                            ESizedInternalFormat.Rg16ui => 4,
                            ESizedInternalFormat.Rgba8 => 4,
                            ESizedInternalFormat.Rgba8Snorm => 4,
                            ESizedInternalFormat.Srgb8Alpha8 => 4,
                            ESizedInternalFormat.Rgba8i => 4,
                            ESizedInternalFormat.Rgba8ui => 4,
                            ESizedInternalFormat.Rgb10A2 => 4,
                            ESizedInternalFormat.R11fG11fB10f => 4,
                            ESizedInternalFormat.Rgb9E5 => 4,
                            ESizedInternalFormat.DepthComponent32f => 4,
                            ESizedInternalFormat.Depth24Stencil8 => 4,

                            // 5-byte formats
                            ESizedInternalFormat.Depth32fStencil8 => 5,

                            // 6-byte formats
                            ESizedInternalFormat.Rgb16f => 6,
                            ESizedInternalFormat.Rgb16Snorm => 6,
                            ESizedInternalFormat.Rgb16i => 6,
                            ESizedInternalFormat.Rgb16ui => 6,

                            // 8-byte formats
                            ESizedInternalFormat.Rg32f => 8,
                            ESizedInternalFormat.Rg32i => 8,
                            ESizedInternalFormat.Rg32ui => 8,
                            ESizedInternalFormat.Rgba16 => 8,
                            ESizedInternalFormat.Rgba16f => 8,
                            ESizedInternalFormat.Rgba16i => 8,
                            ESizedInternalFormat.Rgba16ui => 8,

                            // 12-byte formats
                            ESizedInternalFormat.Rgb32f => 12,
                            ESizedInternalFormat.Rgb32i => 12,
                            ESizedInternalFormat.Rgb32ui => 12,

                            // 16-byte formats
                            ESizedInternalFormat.Rgba32f => 16,
                            ESizedInternalFormat.Rgba32i => 16,
                            ESizedInternalFormat.Rgba32ui => 16,

                            // Default fallback (estimate 4 bytes for unknown formats)
                            _ => 4
                        };
                    }

                    /// <summary>
                    /// Gets the bytes per pixel for a given render buffer storage format.
                    /// </summary>
                    public static int GetBytesPerPixel(ERenderBufferStorage format)
                    {
                        return format switch
                        {
                            // 1-byte formats
                            ERenderBufferStorage.R8 => 1,
                            ERenderBufferStorage.R8i => 1,
                            ERenderBufferStorage.R8ui => 1,
                            ERenderBufferStorage.StencilIndex1 => 1,
                            ERenderBufferStorage.StencilIndex4 => 1,
                            ERenderBufferStorage.StencilIndex8 => 1,

                            // 2-byte formats
                            ERenderBufferStorage.R16 => 2,
                            ERenderBufferStorage.R16f => 2,
                            ERenderBufferStorage.R16i => 2,
                            ERenderBufferStorage.R16ui => 2,
                            ERenderBufferStorage.DepthComponent16 => 2,
                            ERenderBufferStorage.StencilIndex16 => 2,

                            // 3-byte formats
                            ERenderBufferStorage.Rgb8 => 3,
                            ERenderBufferStorage.Srgb8 => 3,
                            ERenderBufferStorage.Rgb8i => 3,
                            ERenderBufferStorage.Rgb8ui => 3,
                            ERenderBufferStorage.DepthComponent24 => 3,

                            // 4-byte formats
                            ERenderBufferStorage.R32f => 4,
                            ERenderBufferStorage.R32i => 4,
                            ERenderBufferStorage.R32ui => 4,
                            ERenderBufferStorage.Rgba8 => 4,
                            ERenderBufferStorage.Srgb8Alpha8 => 4,
                            ERenderBufferStorage.Rgba8i => 4,
                            ERenderBufferStorage.Rgba8ui => 4,
                            ERenderBufferStorage.Rgb10A2 => 4,
                            ERenderBufferStorage.Rgb10A2ui => 4,
                            ERenderBufferStorage.R11fG11fB10f => 4,
                            ERenderBufferStorage.Rgb9E5 => 4,
                            ERenderBufferStorage.DepthComponent32 => 4,
                            ERenderBufferStorage.DepthComponent32f => 4,
                            ERenderBufferStorage.Depth24Stencil8 => 4,
                            ERenderBufferStorage.DepthComponent => 4,
                            ERenderBufferStorage.DepthStencil => 4,

                            // 5-byte formats
                            ERenderBufferStorage.Depth32fStencil8 => 5,

                            // 6-byte formats
                            ERenderBufferStorage.Rgb16 => 6,
                            ERenderBufferStorage.Rgb16f => 6,
                            ERenderBufferStorage.Rgb16i => 6,
                            ERenderBufferStorage.Rgb16ui => 6,

                            // 8-byte formats
                            ERenderBufferStorage.Rgba16 => 8,
                            ERenderBufferStorage.Rgba16f => 8,
                            ERenderBufferStorage.Rgba16i => 8,
                            ERenderBufferStorage.Rgba16ui => 8,

                            // 12-byte formats
                            ERenderBufferStorage.Rgb32f => 12,
                            ERenderBufferStorage.Rgb32i => 12,
                            ERenderBufferStorage.Rgb32ui => 12,

                            // 16-byte formats
                            ERenderBufferStorage.Rgba32f => 16,
                            ERenderBufferStorage.Rgba32i => 16,
                            ERenderBufferStorage.Rgba32ui => 16,

                            // Default fallback (estimate 4 bytes for unknown formats)
                            _ => 4
                        };
                    }
                }
            }
        }
    }
}
