using ImageMagick;
using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using Shouldly;
using XREngine;
using XREngine.Components.Scene.Mesh;
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
                MinVisibleDistance: 1.0f,
                MaxProjectedPixelSpan: 1024.0f,
                MaxScreenCoverage: 0.5f,
                UvDensityHint: 1.0f,
                SamplerName: "diffuseTexture"),
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
                PreviewMaxDimension: 64u,
                PageSelection: SparseTextureStreamingPageSelection.Full),
            desiredResidentSize: 1024u,
            availableManagedBytes: justUnder512Budget);

        fitted.ShouldBe(256u);
    }

    [Test]
    public void DetermineDesiredResidentSize_UsesProjectedPixelSpanAndRoleWeight()
    {
        uint albedoDesired = ImportedTextureStreamingManager.DetermineDesiredResidentSize(
            new ImportedTextureStreamingPolicyInput(
                SourceWidth: 4096u,
                SourceHeight: 4096u,
                ResidentMaxDimension: 512u,
                PreviewReady: true,
                LastVisibleFrameId: 24L,
                MinVisibleDistance: 3.0f,
                MaxProjectedPixelSpan: 1400.0f,
                MaxScreenCoverage: 0.35f,
                UvDensityHint: 1.0f,
                SamplerName: "diffuseTexture"),
            frameId: 24L,
            allowPromotions: true,
            previewMaxDimension: 64u);

        uint roughnessDesired = ImportedTextureStreamingManager.DetermineDesiredResidentSize(
            new ImportedTextureStreamingPolicyInput(
                SourceWidth: 4096u,
                SourceHeight: 4096u,
                ResidentMaxDimension: 512u,
                PreviewReady: true,
                LastVisibleFrameId: 24L,
                MinVisibleDistance: 3.0f,
                MaxProjectedPixelSpan: 1400.0f,
                MaxScreenCoverage: 0.35f,
                UvDensityHint: 1.0f,
                SamplerName: "roughnessTexture"),
            frameId: 24L,
            allowPromotions: true,
            previewMaxDimension: 64u);

        albedoDesired.ShouldBeGreaterThan(roughnessDesired);
        albedoDesired.ShouldBe(2048u);
        roughnessDesired.ShouldBe(1024u);
    }

    [Test]
    public void CalculatePromotionFadeBias_UsesPromotedMipDelta()
    {
        float lodBias = ImportedTextureStreamingManager.CalculatePromotionFadeBias(
            sourceWidth: 4096u,
            sourceHeight: 4096u,
            previousResidentSize: 256u,
            nextResidentSize: 1024u);

        lodBias.ShouldBe(2.0f);
    }

    [Test]
    public void CalculatePromotionFadeBias_DemotionOrNoChange_ReturnsZero()
    {
        ImportedTextureStreamingManager.CalculatePromotionFadeBias(4096u, 4096u, 1024u, 1024u).ShouldBe(0.0f);
        ImportedTextureStreamingManager.CalculatePromotionFadeBias(4096u, 4096u, 1024u, 256u).ShouldBe(0.0f);
    }

    [Test]
    public void ShouldRecordImportedTextureStreamingUsage_OnlyAllowsMainNonShadowPass()
    {
        RenderableMesh.ShouldRecordImportedTextureStreamingUsage(isShadowPass: false, isMainPass: true).ShouldBeTrue();
        RenderableMesh.ShouldRecordImportedTextureStreamingUsage(isShadowPass: true, isMainPass: true).ShouldBeFalse();
        RenderableMesh.ShouldRecordImportedTextureStreamingUsage(isShadowPass: false, isMainPass: false).ShouldBeFalse();
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
    public void TextureYamlAsset_InvalidCookedPayload_ReturnsFalse()
    {
        const string yaml = """
Format: CookedBinary
Payload:
  Length: 4
  Encoding: RawHex
  Bytes: DEADBEEF
""";

        byte[] assetBytes = Encoding.UTF8.GetBytes(yaml);

        XRTexture2D.TryReadResidentDataFromTextureAssetFileBytes(
            assetBytes,
            maxResidentDimension: 4u,
            includeMipChain: true,
            out _).ShouldBeFalse();
    }

    [Test]
    public void AssetTextureStreamingSource_WhenCacheAssetUnreadable_FallsBackToOriginalSource()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"ImportedTextureStreamingPhaseTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            string sourcePath = Path.Combine(tempDirectory, "fallback-source.png");
            string assetPath = Path.Combine(tempDirectory, "fallback-source.png.XREngine.Rendering.XRTexture2D.asset");

            using (MagickImage source = new(MagickColors.Green, 8, 4))
                source.Write(sourcePath);

            File.WriteAllText(assetPath, """
Format: CookedBinary
Payload:
  Length: 4
  Encoding: RawHex
  Bytes: DEADBEEF
""");

            XRTexture2D.IsTextureStreamingAssetUsable(assetPath).ShouldBeFalse();

            AssetTextureStreamingSource streamingSource = new(assetPath, sourcePath);
            TextureStreamingResidentData residentData = streamingSource.LoadResidentData(maxResidentDimension: 4u, includeMipChain: true);

            residentData.SourceWidth.ShouldBe(8u);
            residentData.SourceHeight.ShouldBe(4u);
            residentData.ResidentMaxDimension.ShouldBe(4u);
            residentData.Mipmaps.Length.ShouldBe(3);
            residentData.Mipmaps[0].Width.ShouldBe(4u);
            residentData.Mipmaps[0].Height.ShouldBe(2u);

            File.Delete(assetPath);

            TextureStreamingResidentData secondResidentData = streamingSource.LoadResidentData(maxResidentDimension: 4u, includeMipChain: false);
            secondResidentData.SourceWidth.ShouldBe(8u);
            secondResidentData.SourceHeight.ShouldBe(4u);
            secondResidentData.Mipmaps.Length.ShouldBe(1);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
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
            sparseNumLevels: 7,
            pageSelection: SparseTextureStreamingPageSelection.Full);

        long expectedBytes = XRTexture2D.EstimateMipRangeBytes(
            sourceWidth: 4096u,
            sourceHeight: 4096u,
            baseMipLevel: 7,
            logicalMipCount: XRTexture2D.GetLogicalMipCount(4096u, 4096u),
            format: XREngine.Data.Rendering.ESizedInternalFormat.Rgba8);

        committedBytes.ShouldBe(expectedBytes);
    }

    [Test]
    public void SparseEstimateCommittedBytes_UsesPartialPageCoverageWhenSelected()
    {
        SparseTextureStreamingSupport support = new(
            SupportsSparseTextures: true,
            SupportsSparseTexture2: false,
            VirtualPageSizeX: 128u,
            VirtualPageSizeY: 128u,
            VirtualPageSizeIndex: 0);

        int requestedBaseMipLevel = XRTexture2D.ResolveResidentBaseMipLevel(4096u, 4096u, 1024u);
        int logicalMipCount = XRTexture2D.GetLogicalMipCount(4096u, 4096u);

        long committedBytes = XRTexture2D.EstimateSparsePageSelectionBytes(
            sourceWidth: 4096u,
            sourceHeight: 4096u,
            requestedBaseMipLevel: requestedBaseMipLevel,
            logicalMipCount: logicalMipCount,
            numSparseLevels: 7,
            support: support,
            selection: SparseTextureStreamingPageSelection.Partial(0.0f, 0.0f, 0.25f, 0.25f),
            format: XREngine.Data.Rendering.ESizedInternalFormat.Rgba8);

        long fullCommittedBytes = XRTexture2D.EstimateSparsePageSelectionBytes(
            sourceWidth: 4096u,
            sourceHeight: 4096u,
            requestedBaseMipLevel: requestedBaseMipLevel,
            logicalMipCount: logicalMipCount,
            numSparseLevels: 7,
            support: support,
            selection: SparseTextureStreamingPageSelection.Full,
            format: XREngine.Data.Rendering.ESizedInternalFormat.Rgba8);

        committedBytes.ShouldBeLessThan(fullCommittedBytes);
    }
}
