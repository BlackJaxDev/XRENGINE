using XREngine.Data.Rendering;

namespace XREngine.Rendering.Resources.Authoring;

/// <summary>
/// The one table of texture formats that authored resources can use, because OpenGL and Vulkan both support them as
/// render targets and sampled textures. The editor format picker and the validator read this table.
/// </summary>
public static class PortableRenderFormats
{
    private static readonly PortableRenderFormat[] _entries =
    [
        new(ESizedInternalFormat.R8, EPixelInternalFormat.R8, EPixelFormat.Red, EPixelType.UnsignedByte, false, false, 1u),
        new(ESizedInternalFormat.Rg8, EPixelInternalFormat.RG8, EPixelFormat.Rg, EPixelType.UnsignedByte, false, false, 2u),
        new(ESizedInternalFormat.Rgba8, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, false, false, 4u),
        new(ESizedInternalFormat.Srgb8Alpha8, EPixelInternalFormat.Srgb8Alpha8, EPixelFormat.Rgba, EPixelType.UnsignedByte, false, false, 4u),
        new(ESizedInternalFormat.Rgb10A2, EPixelInternalFormat.Rgb10A2, EPixelFormat.Rgba, EPixelType.UnsignedInt1010102, false, false, 4u),
        new(ESizedInternalFormat.R11fG11fB10f, EPixelInternalFormat.R11fG11fB10f, EPixelFormat.Rgb, EPixelType.UnsignedInt10F11F11FRev, false, false, 4u),
        new(ESizedInternalFormat.R16f, EPixelInternalFormat.R16f, EPixelFormat.Red, EPixelType.HalfFloat, false, false, 2u),
        new(ESizedInternalFormat.Rg16f, EPixelInternalFormat.RG16f, EPixelFormat.Rg, EPixelType.HalfFloat, false, false, 4u),
        new(ESizedInternalFormat.Rgba16f, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, false, false, 8u),
        new(ESizedInternalFormat.R32f, EPixelInternalFormat.R32f, EPixelFormat.Red, EPixelType.Float, false, false, 4u),
        new(ESizedInternalFormat.Rg32f, EPixelInternalFormat.RG32f, EPixelFormat.Rg, EPixelType.Float, false, false, 8u),
        new(ESizedInternalFormat.Rgba32f, EPixelInternalFormat.Rgba32f, EPixelFormat.Rgba, EPixelType.Float, false, false, 16u),
        new(ESizedInternalFormat.R32ui, EPixelInternalFormat.R32ui, EPixelFormat.RedInteger, EPixelType.UnsignedInt, false, false, 4u),
        new(ESizedInternalFormat.DepthComponent32f, EPixelInternalFormat.DepthComponent32f, EPixelFormat.DepthComponent, EPixelType.Float, true, false, 4u),
        new(ESizedInternalFormat.Depth24Stencil8, EPixelInternalFormat.Depth24Stencil8, EPixelFormat.DepthStencil, EPixelType.UnsignedInt248, true, true, 4u),
        new(ESizedInternalFormat.Depth32fStencil8, EPixelInternalFormat.Depth32fStencil8, EPixelFormat.DepthStencil, EPixelType.Float32UnsignedInt248Rev, true, true, 8u),
    ];

    /// <summary>Gets every portable format in picker order.</summary>
    public static IReadOnlyList<PortableRenderFormat> Entries => _entries;

    /// <summary>Finds the table entry of a sized format.</summary>
    public static bool TryGet(ESizedInternalFormat format, out PortableRenderFormat? entry)
    {
        for (int i = 0; i < _entries.Length; i++)
        {
            if (_entries[i].Sized == format)
            {
                entry = _entries[i];
                return true;
            }
        }

        entry = null;
        return false;
    }

    /// <summary>Returns whether the format is in the portable table.</summary>
    public static bool IsPortable(ESizedInternalFormat format)
        => TryGet(format, out _);

    /// <summary>
    /// Returns whether a format is three-channel without packing. Vulkan and WebGPU make these formats optional, and
    /// most devices cannot render to them, so the table leaves them out.
    /// </summary>
    public static bool IsUnpackedThreeChannel(ESizedInternalFormat format)
        => format is ESizedInternalFormat.Rgb4 or ESizedInternalFormat.Rgb5 or ESizedInternalFormat.Rgb8 or
            ESizedInternalFormat.Rgb8Snorm or ESizedInternalFormat.Rgb10 or ESizedInternalFormat.Rgb12 or
            ESizedInternalFormat.Rgb16Snorm or ESizedInternalFormat.Srgb8 or ESizedInternalFormat.Rgb16f or
            ESizedInternalFormat.Rgb32f or ESizedInternalFormat.Rgb8i or ESizedInternalFormat.Rgb8ui or
            ESizedInternalFormat.Rgb16i or ESizedInternalFormat.Rgb16ui or ESizedInternalFormat.Rgb32i or
            ESizedInternalFormat.Rgb32ui;
}
