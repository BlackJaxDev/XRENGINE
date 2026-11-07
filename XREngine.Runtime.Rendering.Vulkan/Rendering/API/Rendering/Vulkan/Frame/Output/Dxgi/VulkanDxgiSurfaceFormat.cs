using Silk.NET.DXGI;
using Silk.NET.Vulkan;
using DxgiFormat = Silk.NET.DXGI.Format;
using VkFormat = Silk.NET.Vulkan.Format;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// One output format of the DXGI flip-model present path. It pairs the Vulkan image
/// format and color space that the frame loop renders with the Direct3D 11 shared
/// texture format, the DXGI back-buffer format, and the DXGI color space.
/// </summary>
/// <param name="VulkanFormat">Format of the shared images that Vulkan renders into.</param>
/// <param name="VulkanColorSpace">Color space that the renderer encodes for this output.</param>
/// <param name="SharedFormat">Format of the shared Direct3D 11 textures. It must match <paramref name="VulkanFormat"/>.</param>
/// <param name="BufferFormat">Format of the DXGI back buffers. It must be copy-compatible with <paramref name="SharedFormat"/>.</param>
/// <param name="DxgiColorSpace">Color space that the DXGI swapchain declares to the compositor.</param>
/// <param name="Name">Short name for logs.</param>
internal readonly record struct VulkanDxgiSurfaceFormat(
    VkFormat VulkanFormat,
    ColorSpaceKHR VulkanColorSpace,
    DxgiFormat SharedFormat,
    DxgiFormat BufferFormat,
    ColorSpaceType DxgiColorSpace,
    string Name)
{
    /// <summary>
    /// 8-bit sRGB output. The shared texture uses the sRGB view so Vulkan writes encoded
    /// values; the copy into the UNORM back buffer keeps the encoded bits.
    /// </summary>
    internal static VulkanDxgiSurfaceFormat Sdr { get; } = new(
        VkFormat.B8G8R8A8Srgb,
        ColorSpaceKHR.SpaceSrgbNonlinearKhr,
        DxgiFormat.FormatB8G8R8A8UnormSrgb,
        DxgiFormat.FormatB8G8R8A8Unorm,
        ColorSpaceType.RgbFullG22NoneP709,
        "SDR B8G8R8A8 sRGB");

    /// <summary>
    /// Linear scRGB output in 16-bit float. This matches the first HDR choice of the
    /// native Vulkan path (R16G16B16A16 float with extended linear sRGB).
    /// </summary>
    internal static VulkanDxgiSurfaceFormat ScRgb { get; } = new(
        VkFormat.R16G16B16A16Sfloat,
        ColorSpaceKHR.SpaceExtendedSrgbLinearExt,
        DxgiFormat.FormatR16G16B16A16Float,
        DxgiFormat.FormatR16G16B16A16Float,
        ColorSpaceType.RgbFullG10NoneP709,
        "HDR scRGB R16G16B16A16 float");

    /// <summary>
    /// HDR10 output with the PQ curve and BT.2020 primaries in 10-bit UNORM. The
    /// Vulkan A2B10G10R10 pack has the same bit layout as DXGI R10G10B10A2.
    /// </summary>
    internal static VulkanDxgiSurfaceFormat Hdr10 { get; } = new(
        VkFormat.A2B10G10R10UnormPack32,
        ColorSpaceKHR.SpaceHdr10ST2084Ext,
        DxgiFormat.FormatR10G10B10A2Unorm,
        DxgiFormat.FormatR10G10B10A2Unorm,
        ColorSpaceType.RgbFullG2084NoneP2020,
        "HDR10 R10G10B10A2 PQ");
}
