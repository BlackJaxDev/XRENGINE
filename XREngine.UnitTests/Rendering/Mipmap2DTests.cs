using ImageMagick;
using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class Mipmap2DTests
{
    [Test]
    public void SetFromImage_PreservesDecodedRgba8ColorData()
    {
        byte[] rgba =
        [
            255, 0, 0, 255,
            0, 128, 255, 64,
        ];

        MagickReadSettings settings = new()
        {
            Width = 2,
            Height = 1,
            Format = MagickFormat.Rgba,
            Depth = 8,
        };

        using MagickImage image = new(rgba, settings);
        Mipmap2D mipmap = new(image);

        byte[] actual = mipmap.DataBytes.ShouldNotBeNull();
        actual.ShouldBe(rgba);
    }

    [Test]
    public void VulkanUploadNormalization_RgbaMipmapToR8_RepackRedChannelTightly()
    {
        byte[] rgba =
        [
            10, 1, 2, 3,
            20, 4, 5, 6,
            30, 7, 8, 9,
            40, 10, 11, 12,
        ];

        Mipmap2D mipmap = new(2, 2, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, allocateData: false)
        {
            Data = new DataSource(rgba)
        };

        DataSource? uploadData = VulkanRenderer.VkFormatConversions.CreateNormalizedUploadData2D(
            mipmap,
            Format.R8Unorm,
            out bool ownsData);

        try
        {
            ownsData.ShouldBeTrue();
            uploadData.ShouldNotBeNull();
            uploadData.ShouldNotBeSameAs(mipmap.Data);
            uploadData.GetBytes().ShouldBe([10, 20, 30, 40]);
        }
        finally
        {
            if (ownsData)
                uploadData?.Dispose();
            mipmap.Data?.Dispose();
        }
    }

    [Test]
    public void VulkanUploadNormalization_RgbMipmapToRgba_PadsOpaqueAlpha()
    {
        Mipmap2D mipmap = new(2, 1, EPixelInternalFormat.Rgb8, EPixelFormat.Rgb, EPixelType.UnsignedByte, allocateData: false)
        {
            Data = new DataSource([1, 2, 3, 4, 5, 6])
        };

        DataSource? uploadData = VulkanRenderer.VkFormatConversions.CreateNormalizedUploadData2D(
            mipmap,
            Format.R8G8B8A8Unorm,
            out bool ownsData);

        try
        {
            ownsData.ShouldBeTrue();
            uploadData.ShouldNotBeNull();
            uploadData.GetBytes().ShouldBe([1, 2, 3, 255, 4, 5, 6, 255]);
        }
        finally
        {
            if (ownsData)
                uploadData?.Dispose();
            mipmap.Data?.Dispose();
        }
    }

    [Test]
    public void VulkanUploadNormalization_RgbaMipmapToRgba_UsesOriginalData()
    {
        Mipmap2D mipmap = new(1, 1, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, allocateData: false)
        {
            Data = new DataSource([1, 2, 3, 4])
        };

        DataSource? uploadData = VulkanRenderer.VkFormatConversions.CreateNormalizedUploadData2D(
            mipmap,
            Format.R8G8B8A8Unorm,
            out bool ownsData);

        try
        {
            ownsData.ShouldBeFalse();
            uploadData.ShouldBeSameAs(mipmap.Data);
        }
        finally
        {
            mipmap.Data?.Dispose();
        }
    }
}
