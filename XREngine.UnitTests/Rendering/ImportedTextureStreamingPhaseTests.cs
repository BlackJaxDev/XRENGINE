using ImageMagick;
using System.Text;
using NUnit.Framework;
using Shouldly;
using XREngine;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class ImportedTextureStreamingPhaseTests
{
    [Test]
    public void DetermineDesiredResidentSize_WhenPromotionsBlocked_ReturnsPreviewResidentSize()
    {
        uint desired = ImportedTextureStreamingManager.DetermineDesiredResidentSize(
            new ImportedTextureStreamingPolicyInput(
                SourceWidth: 4096u,
                SourceHeight: 4096u,
                ResidentMaxDimension: 64u,
                PreviewReady: true,
                LastVisibleFrameId: 12L,
                MinVisibleDistance: 1.0f),
            frameId: 12L,
            allowPromotions: false,
            previewMaxDimension: 64u);

        desired.ShouldBe(64u);
    }

    [Test]
    public void FitResidentSizeToBudget_DemotesUntilResidentBytesFit()
    {
        long justUnder512Budget = XRTexture2D.EstimateResidentBytes(1024u, 1024u, 512u) - 1L;

        uint fitted = ImportedTextureStreamingManager.Instance.FitResidentSizeToBudget(
            new ImportedTextureStreamingBudgetInput(
                SourceWidth: 1024u,
                SourceHeight: 1024u,
                ResidentMaxDimension: 64u,
                PreviewMaxDimension: 64u),
            desiredResidentSize: 1024u,
            availableManagedBytes: justUnder512Budget);

        fitted.ShouldBe(256u);
    }

    [Test]
    public void StreamableTexturePayload_ReadsOnlyRequestedResidentSlice()
    {
        using MagickImage source = new(MagickColors.Red, 8, 4);
        byte[] payload = XRTexture2D.CreateTextureStreamingPayload("payload-test.png", source);

        XRTexture2D.TryReadResidentDataFromTextureStreamingPayload(
            payload,
            maxResidentDimension: 4u,
            includeMipChain: true,
            out TextureStreamingResidentData residentData).ShouldBeTrue();

        residentData.SourceWidth.ShouldBe(8u);
        residentData.SourceHeight.ShouldBe(4u);
        residentData.ResidentMaxDimension.ShouldBe(4u);
        residentData.Mipmaps.Length.ShouldBe(3);
        residentData.Mipmaps[0].Width.ShouldBe(4u);
        residentData.Mipmaps[0].Height.ShouldBe(2u);
        residentData.Mipmaps[^1].Width.ShouldBe(1u);
        residentData.Mipmaps[^1].Height.ShouldBe(1u);
    }

    [Test]
    public void TextureYamlAsset_ReadsResidentSliceFromCookedEnvelope()
    {
        using MagickImage source = new(MagickColors.Blue, 8, 4);
        XRTexture2D texture = new()
        {
            Name = "EnvelopeTexture",
            FilePath = Path.Combine(Path.GetTempPath(), "EnvelopeTexture.asset"),
            Mipmaps = XRTexture2D.GetMipmapsFromImage(source)
        };

        string yaml = AssetManager.Serializer.Serialize(texture);
        yaml.ShouldContain("Format: CookedBinary");

        byte[] assetBytes = Encoding.UTF8.GetBytes(yaml);
        XRTexture2D.TryReadResidentDataFromTextureAssetFileBytes(
            assetBytes,
            maxResidentDimension: 4u,
            includeMipChain: true,
            out TextureStreamingResidentData residentData).ShouldBeTrue();

        residentData.SourceWidth.ShouldBe(8u);
        residentData.SourceHeight.ShouldBe(4u);
        residentData.ResidentMaxDimension.ShouldBe(4u);
        residentData.Mipmaps.Length.ShouldBe(3);
        residentData.Mipmaps[0].Width.ShouldBe(4u);
        residentData.Mipmaps[0].Height.ShouldBe(2u);

        XRTexture2D roundTripped = AssetManager.Deserializer.Deserialize<XRTexture2D>(yaml);
        roundTripped.Width.ShouldBe(8u);
        roundTripped.Height.ShouldBe(4u);
        roundTripped.Mipmaps.Length.ShouldBe(texture.Mipmaps.Length);
    }

    [Test]
    public void SparseTextureSupport_PageAlignmentUsesVirtualPageSize()
    {
        SparseTextureStreamingSupport support = new(
            SupportsSparseTextures: true,
            SupportsSparseTexture2: false,
            VirtualPageSizeX: 128u,
            VirtualPageSizeY: 128u,
            VirtualPageSizeIndex: 0);

        support.IsPageAligned(4096u, 2048u).ShouldBeTrue();
        support.IsPageAligned(4100u, 2048u).ShouldBeFalse();
        support.IsPageAligned(4096u, 2050u).ShouldBeFalse();
    }

    [Test]
    public void SparseEstimateCommittedBytes_UsesMipTailBoundary()
    {
        GLSparseTextureResidencyBackend backend = new();

        long committedBytes = backend.EstimateCommittedBytes(
            sourceWidth: 4096u,
            sourceHeight: 4096u,
            residentMaxDimension: 16u,
            format: XREngine.Data.Rendering.ESizedInternalFormat.Rgba8,
            sparseNumLevels: 7);

        long expectedBytes = XRTexture2D.EstimateMipRangeBytes(
            sourceWidth: 4096u,
            sourceHeight: 4096u,
            baseMipLevel: 7,
            logicalMipCount: XRTexture2D.GetLogicalMipCount(4096u, 4096u),
            format: XREngine.Data.Rendering.ESizedInternalFormat.Rgba8);

        committedBytes.ShouldBe(expectedBytes);
    }
}
