using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RenderQuadDestinationRenderAreaTests
{
    [Test]
    public void RenderQuadToFbo_DestinationRenderArea_UsesAttachmentSize()
    {
        XRTexture2D halfRes = XRTexture2D.CreateFrameBufferTexture(
            1280,
            720,
            EPixelInternalFormat.R16f,
            EPixelFormat.Red,
            EPixelType.HalfFloat,
            EFrameBufferAttachment.ColorAttachment0);
        XRFrameBuffer fbo = new((halfRes, EFrameBufferAttachment.ColorAttachment0, 0, -1));

        VPRC_RenderQuadToFBO.TryResolveDestinationRenderArea(fbo, out int width, out int height).ShouldBeTrue();

        width.ShouldBe(1280);
        height.ShouldBe(720);
    }

    [Test]
    public void RenderQuadToFbo_DestinationRenderArea_UsesMipExtent()
    {
        XRTexture2D fullRes = XRTexture2D.CreateFrameBufferTexture(
            256,
            128,
            EPixelInternalFormat.Rgba16f,
            EPixelFormat.Rgba,
            EPixelType.HalfFloat,
            EFrameBufferAttachment.ColorAttachment0);
        XRFrameBuffer fbo = new((fullRes, EFrameBufferAttachment.ColorAttachment0, 1, -1));

        VPRC_RenderQuadToFBO.TryResolveDestinationRenderArea(fbo, out int width, out int height).ShouldBeTrue();

        width.ShouldBe(128);
        height.ShouldBe(64);
    }

    [Test]
    public void RenderQuadToFbo_StereoDestinationRenderArea_PreservesOddEyeHeightAtEveryMip()
    {
        XRTexture2DArray eyeArray = XRTexture2DArray.CreateFrameBufferTexture(
            2u,
            896u,
            1007u,
            EPixelInternalFormat.Rgba16f,
            EPixelFormat.Rgba,
            EPixelType.HalfFloat,
            EFrameBufferAttachment.ColorAttachment0);
        eyeArray.OVRMultiViewParameters = new(0, 2u);

        XRFrameBuffer full = new((eyeArray, EFrameBufferAttachment.ColorAttachment0, 0, -1));
        VPRC_RenderQuadToFBO.TryResolveDestinationRenderArea(full, out int fullWidth, out int fullHeight).ShouldBeTrue();
        fullWidth.ShouldBe(896);
        fullHeight.ShouldBe(1007);

        XRFrameBuffer mip4 = new((eyeArray, EFrameBufferAttachment.ColorAttachment0, 4, -1));
        VPRC_RenderQuadToFBO.TryResolveDestinationRenderArea(mip4, out int mipWidth, out int mipHeight).ShouldBeTrue();
        mipWidth.ShouldBe(56);
        mipHeight.ShouldBe(62);
    }

    [Test]
    public void VulkanFramebufferDrawExtent_UsesSmallestAttachmentMipExtent()
    {
        XRTexture2D fullRes = XRTexture2D.CreateFrameBufferTexture(
            512,
            256,
            EPixelInternalFormat.Rgba16f,
            EPixelFormat.Rgba,
            EPixelType.HalfFloat,
            EFrameBufferAttachment.ColorAttachment0);
        XRTexture2D halfRes = XRTexture2D.CreateFrameBufferTexture(
            256,
            128,
            EPixelInternalFormat.R16f,
            EPixelFormat.Red,
            EPixelType.HalfFloat,
            EFrameBufferAttachment.ColorAttachment1);
        XRFrameBuffer fbo = new(
            (fullRes, EFrameBufferAttachment.ColorAttachment0, 1, -1),
            (halfRes, EFrameBufferAttachment.ColorAttachment1, 0, -1));

        Extent2D extent = VulkanRenderer.ResolveFrameBufferDrawExtent(fbo);

        extent.Width.ShouldBe(256u);
        extent.Height.ShouldBe(128u);
    }
}
