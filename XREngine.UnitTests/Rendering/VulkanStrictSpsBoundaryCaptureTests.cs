using NUnit.Framework;
using Silk.NET.Vulkan;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanStrictSpsBoundaryCaptureTests
{
    [Test]
    public void BoundaryCapture_MapsStereoLogicalEyesToDistinctArrayLayers()
    {
        XRTexture2DArray stereoArray = CreateStereoArray();

        OpenXRAPI.TryResolveStrictSpsBoundaryReadbackLayer(
                stereoArray,
                logicalLayerIndex: 0,
                expectedLayerCount: 2,
                out int leftLayer,
                out string leftFailure)
            .ShouldBeTrue(leftFailure);
        OpenXRAPI.TryResolveStrictSpsBoundaryReadbackLayer(
                stereoArray,
                logicalLayerIndex: 1,
                expectedLayerCount: 2,
                out int rightLayer,
                out string rightFailure)
            .ShouldBeTrue(rightFailure);

        leftLayer.ShouldBe(0);
        rightLayer.ShouldBe(1);
        leftLayer.ShouldNotBe(rightLayer);
    }

    [Test]
    public void BoundaryCapture_RejectsSingleLayerViewClaimingStereoAttribution()
    {
        XRTexture2DArray stereoArray = CreateStereoArray();
        var rightView = new XRTexture2DArrayView(
            stereoArray,
            minLevel: 0u,
            numLevels: 1u,
            minLayer: 1u,
            numLayers: 1u,
            internalFormat: stereoArray.SizedInternalFormat,
            array: false,
            multisample: false);

        OpenXRAPI.TryResolveStrictSpsBoundaryReadbackLayer(
                rightView,
                logicalLayerIndex: 1,
                expectedLayerCount: 2,
                out _,
                out string failure)
            .ShouldBeFalse();

        failure.ShouldContain("exposes 1 layer(s), expected exactly 2");
    }

    [Test]
    public void DescriptorReadback_PreservesAbsoluteBackingLayerOfArrayViews()
    {
        XRTexture2DArray stereoArray = CreateStereoArray();
        var leftView = new XRTexture2DArrayView(
            stereoArray,
            minLevel: 0u,
            numLevels: 1u,
            minLayer: 0u,
            numLayers: 1u,
            internalFormat: stereoArray.SizedInternalFormat,
            array: false,
            multisample: false);
        var rightView = new XRTexture2DArrayView(
            stereoArray,
            minLevel: 0u,
            numLevels: 1u,
            minLayer: 1u,
            numLayers: 1u,
            internalFormat: stereoArray.SizedInternalFormat,
            array: false,
            multisample: false);

        var leftRange = VulkanRenderer.ResolveDescriptorTextureBlitLayerRange(
            leftView,
            layerIndex: 0,
            descriptorArrayLayers: 1u);
        var rightRange = VulkanRenderer.ResolveDescriptorTextureBlitLayerRange(
            rightView,
            layerIndex: 0,
            descriptorArrayLayers: 1u);

        leftRange.BaseArrayLayer.ShouldBe(0u);
        leftRange.LayerCount.ShouldBe(1u);
        rightRange.BaseArrayLayer.ShouldBe(1u);
        rightRange.LayerCount.ShouldBe(1u);
    }

    [Test]
    public void BoundaryReadback_RestoresColorAttachmentLayoutForPublishStaging()
    {
        var stagingState = OpenXRAPI.ResolveStrictSpsBoundaryCaptureSourceState("PublishStaging");
        stagingState.Layout.ShouldBe(ImageLayout.ColorAttachmentOptimal);
        stagingState.Stage.ShouldBe(PipelineStageFlags.ColorAttachmentOutputBit);
        stagingState.Access.ShouldBe(
            AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit);

        VulkanRenderer.ResolveReadbackRestoreLayout(
                stagingState.Layout,
                ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit,
                depthOrStencil: false)
            .ShouldBe(ImageLayout.ColorAttachmentOptimal);
    }

    [Test]
    public void ReadbackRestore_UsesUsageFallbackOnlyWhenPreTransferLayoutIsUnknown()
    {
        VulkanRenderer.ResolveReadbackRestoreLayout(
                ImageLayout.Undefined,
                ImageUsageFlags.SampledBit,
                depthOrStencil: false)
            .ShouldBe(ImageLayout.ShaderReadOnlyOptimal);
        VulkanRenderer.ResolveReadbackRestoreLayout(
                ImageLayout.Undefined,
                ImageUsageFlags.StorageBit,
                depthOrStencil: false)
            .ShouldBe(ImageLayout.General);
    }

    private static XRTexture2DArray CreateStereoArray()
        => new(
            count: 2u,
            width: 8u,
            height: 8u,
            internalFormat: EPixelInternalFormat.Rgba8,
            format: EPixelFormat.Rgba,
            type: EPixelType.UnsignedByte);
}
