using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Checks which texture changes after an Advanced publication the Vulkan source check may
/// accept: a dense streaming resident commit (other mip count, extent, or format inside one
/// format family) and the replacement of the loading placeholder are accepted, while a shape
/// change, a format change outside the family or a capture-output content change is rejected.
/// </summary>
[TestFixture]
public sealed class AdvancedTextureResidencyDriftTests
{
    [Test]
    public void DenseResidentCommit_IsResidencyOnly_AndKeepsTheShape()
    {
        XRTexture2D texture = CreateTexture(mipCount: 5);
        AdvancedGpuResourceBindingSource published = Encode(texture);
        ulong captured = published.SourceContentGeneration;

        CommitResident(texture, mipCount: 4);
        AdvancedGpuResourceBindingSource current = Encode(texture);

        current.SourceContentGeneration.ShouldNotBe(captured);
        AdvancedTextureResidencyDrift.IsResidencyOnlyChange(texture, captured, current.SourceContentGeneration).ShouldBeTrue();
        current.TextureRecord.MipCount.ShouldNotBe(published.TextureRecord.MipCount);
        AdvancedTextureResidencyDrift.TextureShapeEquals(current.TextureRecord, published.TextureRecord).ShouldBeTrue();
        AdvancedTextureResidencyDrift.SamplerShapeEquals(current.SamplerRecord, published.SamplerRecord).ShouldBeTrue();

        AdvancedTextureRecord row = published.TextureRecord;
        AdvancedTextureResidencyDrift.ApplyResidency(ref row, current.TextureRecord);
        row.MipCount.ShouldBe(current.TextureRecord.MipCount);
        row.Width.ShouldBe(current.TextureRecord.Width);
        row.Height.ShouldBe(current.TextureRecord.Height);
    }

    [Test]
    public void UnchangedGeneration_IsNotResidencyDrift()
    {
        XRTexture2D texture = CreateTexture(mipCount: 3);
        ulong captured = texture.CanonicalSourceContentGeneration;

        AdvancedTextureResidencyDrift.IsResidencyOnlyChange(texture, captured, captured).ShouldBeFalse();
    }

    [Test]
    public void CaptureOutputGeneration_IsNotResidencyOnly()
    {
        XRTexture2D texture = CreateTexture(mipCount: 3);
        ulong captured = texture.CanonicalSourceContentGeneration;

        ulong current = texture.AdvanceCanonicalGpuContentGeneration();

        AdvancedTextureResidencyDrift.IsResidencyOnlyChange(texture, captured, current).ShouldBeFalse();
    }

    [Test]
    public void CaptureOutputAfterResidentCommit_IsNotResidencyOnly()
    {
        XRTexture2D texture = CreateTexture(mipCount: 5);
        ulong captured = texture.CanonicalSourceContentGeneration;

        CommitResident(texture, mipCount: 4);
        ulong current = texture.AdvanceCanonicalGpuContentGeneration();

        AdvancedTextureResidencyDrift.IsResidencyOnlyChange(texture, captured, current).ShouldBeFalse();
    }

    [Test]
    public void ResidentCommitAfterCaptureOutput_IsResidencyOnlySinceTheCapture()
    {
        XRTexture2D texture = CreateTexture(mipCount: 5);
        ulong captured = texture.AdvanceCanonicalGpuContentGeneration();

        CommitResident(texture, mipCount: 4);

        AdvancedTextureResidencyDrift.IsResidencyOnlyChange(texture, captured, texture.CanonicalSourceContentGeneration).ShouldBeTrue();
    }

    [Test]
    public void TextureShapeChange_IsRejected()
    {
        AdvancedTextureRecord published = new()
        {
            Dimension = EAdvancedTextureDimension.Texture2D,
            Width = 16u,
            Height = 16u,
            DepthOrLayers = 1u,
            MipCount = 5u,
            FormatClass = 1u,
            UvScaleBias = new Vector4(1f, 1f, 0f, 0f),
        };

        AdvancedTextureRecord residency = published with { Width = 8u, Height = 8u, MipCount = 4u };
        AdvancedTextureResidencyDrift.TextureShapeEquals(residency, published).ShouldBeTrue();

        AdvancedTextureResidencyDrift.TextureShapeEquals(published with { FormatClass = 2u }, published).ShouldBeFalse();
        AdvancedTextureResidencyDrift.TextureShapeEquals(published with { Flags = EAdvancedTextureRecordFlags.Srgb }, published).ShouldBeFalse();
        AdvancedTextureResidencyDrift.TextureShapeEquals(published with { Dimension = EAdvancedTextureDimension.Texture2DArray }, published).ShouldBeFalse();
        AdvancedTextureResidencyDrift.TextureShapeEquals(published with { DepthOrLayers = 2u }, published).ShouldBeFalse();
    }

    [Test]
    public void SamplerShapeChange_IsRejected_AndLodClampChangeIsAccepted()
    {
        AdvancedSamplerRecord published = new()
        {
            Filter = EAdvancedSamplerFilter.Linear,
            AddressU = EAdvancedSamplerAddressMode.Repeat,
            AddressV = EAdvancedSamplerAddressMode.Repeat,
            AddressW = EAdvancedSamplerAddressMode.ClampToEdge,
            CompareOperation = EAdvancedCompareOperation.Never,
            LodBiasMinMaxAnisotropy = new Vector4(0f, 0f, 4f, 8f),
            BorderColor = new Vector4(0f, 0f, 0f, 1f),
        };

        AdvancedSamplerRecord lodClamp = published with { LodBiasMinMaxAnisotropy = new Vector4(0f, 0f, 3f, 8f) };
        AdvancedTextureResidencyDrift.SamplerShapeEquals(lodClamp, published).ShouldBeTrue();

        AdvancedTextureResidencyDrift.SamplerShapeEquals(published with { AddressU = EAdvancedSamplerAddressMode.ClampToEdge }, published).ShouldBeFalse();
        AdvancedTextureResidencyDrift.SamplerShapeEquals(published with { Filter = EAdvancedSamplerFilter.Nearest }, published).ShouldBeFalse();
        AdvancedTextureResidencyDrift.SamplerShapeEquals(
            published with { LodBiasMinMaxAnisotropy = new Vector4(1f, 0f, 4f, 8f) }, published).ShouldBeFalse();
        AdvancedTextureResidencyDrift.SamplerShapeEquals(
            published with { LodBiasMinMaxAnisotropy = new Vector4(0f, 0f, 4f, 1f) }, published).ShouldBeFalse();
    }

    [Test]
    public void ReadinessTracker_ChangedMipCount_AdvancesGeneration()
    {
        VulkanAdvancedTextureReadinessTracker tracker = new();
        tracker.Allocate(2);
        AdvancedTextureRecord[] rows = [new AdvancedTextureRecord { EncodedReferenceIndex = 1u, Width = 16u, Height = 16u, MipCount = 5u }];

        tracker.Update(rows).ShouldBeTrue();
        ulong before = tracker.Generation;
        tracker.Update(rows).ShouldBeFalse();

        rows[0].MipCount = 4u;

        tracker.Update(rows).ShouldBeTrue();
        tracker.Generation.ShouldNotBe(before);
    }

    [Test]
    public void PlaceholderPublication_ToFirstResidentCommit_IsPlaceholderDrift()
    {
        XRTexture2D texture = new() { Mipmaps = CreateChain(1) };
        texture.MarkAwaitingFirstResidency();
        ulong captured = texture.CanonicalSourceContentGeneration;

        CommitResident(texture, mipCount: 5);

        AdvancedTextureResidencyDrift.Classify(texture, captured, texture.CanonicalSourceContentGeneration)
            .ShouldBe(EAdvancedTextureDriftKind.Placeholder);

        // A later resident commit keeps the placeholder classification for the old publication.
        CommitResident(texture, mipCount: 4);
        AdvancedTextureResidencyDrift.Classify(texture, captured, texture.CanonicalSourceContentGeneration)
            .ShouldBe(EAdvancedTextureDriftKind.Placeholder);

        // A capture output after the first residency needs the exact check again.
        ulong current = texture.AdvanceCanonicalGpuContentGeneration();
        AdvancedTextureResidencyDrift.Classify(texture, captured, current).ShouldBe(EAdvancedTextureDriftKind.Exact);
    }

    [Test]
    public void PlaceholderToBc4_IsAccepted_AndTheRowIsReplaced()
    {
        AdvancedTextureRecord placeholder = Record(EAdvancedTextureFormatClass.Rgb8, extent: 8u, mips: 1u);
        placeholder.StableTextureId = 141u;
        placeholder.Generation = 1u;
        placeholder.EncodedReferenceIndex = 9u;
        AdvancedTextureRecord current = Record(EAdvancedTextureFormatClass.Bc4Unorm, extent: 64u, mips: 1u);

        AdvancedTextureResidencyDrift.CompareTexture(EAdvancedTextureDriftKind.Placeholder, true, current, placeholder)
            .ShouldBe(EAdvancedTextureSourceMismatch.None);
        AdvancedTextureResidencyDrift.SamplerEquals(EAdvancedTextureDriftKind.Placeholder, default, Sampler()).ShouldBeTrue();

        AdvancedTextureRecord row = placeholder;
        AdvancedTextureResidencyDrift.Apply(EAdvancedTextureDriftKind.Placeholder, ref row, current);
        row.FormatClass.ShouldBe((uint)EAdvancedTextureFormatClass.Bc4Unorm);
        row.Width.ShouldBe(64u);
        row.Height.ShouldBe(64u);
        row.StableTextureId.ShouldBe(141u);
        row.Generation.ShouldBe(1u);
        row.EncodedReferenceIndex.ShouldBe(9u);
    }

    [Test]
    public void PlaceholderShapeChange_IsRejected()
    {
        AdvancedTextureRecord placeholder = Record(EAdvancedTextureFormatClass.Rgb8, extent: 8u, mips: 1u);

        AdvancedTextureResidencyDrift.CompareTexture(
                EAdvancedTextureDriftKind.Placeholder, true,
                placeholder with { Dimension = EAdvancedTextureDimension.Texture2DArray }, placeholder)
            .ShouldBe(EAdvancedTextureSourceMismatch.TextureShapeChanged);
        AdvancedTextureResidencyDrift.CompareTexture(
                EAdvancedTextureDriftKind.Placeholder, true,
                placeholder with { Flags = EAdvancedTextureRecordFlags.Depth }, placeholder)
            .ShouldBe(EAdvancedTextureSourceMismatch.TextureShapeChanged);
    }

    [TestCase(EAdvancedTextureFormatClass.Rgb8, EAdvancedTextureFormatClass.Bc4Unorm, EAdvancedTextureSourceMismatch.None)]
    [TestCase(EAdvancedTextureFormatClass.Rgba8, EAdvancedTextureFormatClass.Bc7Unorm, EAdvancedTextureSourceMismatch.None)]
    [TestCase(EAdvancedTextureFormatClass.Rgb8, EAdvancedTextureFormatClass.Bc7Unorm, EAdvancedTextureSourceMismatch.None)]
    [TestCase(EAdvancedTextureFormatClass.Srgb8, EAdvancedTextureFormatClass.Bc7Srgb, EAdvancedTextureSourceMismatch.None)]
    [TestCase(EAdvancedTextureFormatClass.Rgb8, EAdvancedTextureFormatClass.Rgba16Float, EAdvancedTextureSourceMismatch.TextureFormatChanged)]
    [TestCase(EAdvancedTextureFormatClass.Bc7Srgb, EAdvancedTextureFormatClass.Rgb8, EAdvancedTextureSourceMismatch.TextureFormatChanged)]
    [TestCase(EAdvancedTextureFormatClass.Rgb8, EAdvancedTextureFormatClass.Bc5Unorm, EAdvancedTextureSourceMismatch.TextureFormatChanged)]
    [TestCase(EAdvancedTextureFormatClass.Rgba8, EAdvancedTextureFormatClass.Rgba8Snorm, EAdvancedTextureSourceMismatch.TextureFormatChanged)]
    public void ResidencyFormatChange_UsesTheFormatFamily(
        EAdvancedTextureFormatClass published,
        EAdvancedTextureFormatClass current,
        EAdvancedTextureSourceMismatch expected)
        => AdvancedTextureResidencyDrift.CompareTexture(
                EAdvancedTextureDriftKind.Residency, true,
                Record(current, extent: 64u, mips: 7u),
                Record(published, extent: 8u, mips: 4u))
            .ShouldBe(expected);

    [Test]
    public void ResidencySrgbFlagChange_IsRejected()
    {
        AdvancedTextureRecord published = Record(EAdvancedTextureFormatClass.Rgb8, extent: 8u, mips: 4u);
        AdvancedTextureRecord current = Record(EAdvancedTextureFormatClass.Bc7Unorm, extent: 64u, mips: 7u) with
        {
            Flags = EAdvancedTextureRecordFlags.Srgb,
        };

        AdvancedTextureResidencyDrift.CompareTexture(EAdvancedTextureDriftKind.Residency, true, current, published)
            .ShouldBe(EAdvancedTextureSourceMismatch.TextureShapeChanged);
    }

    [Test]
    public void ExactGenerationChange_IsContentGenerationChanged()
    {
        AdvancedTextureRecord record = Record(EAdvancedTextureFormatClass.Rgba8, extent: 16u, mips: 5u);

        AdvancedTextureResidencyDrift.CompareTexture(EAdvancedTextureDriftKind.Exact, true, record, record)
            .ShouldBe(EAdvancedTextureSourceMismatch.ContentGenerationChanged);
        AdvancedTextureResidencyDrift.CompareTexture(EAdvancedTextureDriftKind.Exact, false, record with { MipCount = 4u }, record)
            .ShouldBe(EAdvancedTextureSourceMismatch.TextureExtentOrMipsChanged);
        AdvancedTextureResidencyDrift.CompareTexture(EAdvancedTextureDriftKind.Exact, false, record, record)
            .ShouldBe(EAdvancedTextureSourceMismatch.None);
    }

    [Test]
    public void SourceMismatchCounter_CountsEachReasonAndTheLast()
    {
        long before = AdvancedTextureSourceMismatchDiagnostics.GetCount(EAdvancedTextureSourceMismatch.TextureFormatChanged);

        AdvancedTextureSourceMismatchDiagnostics.Note(EAdvancedTextureSourceMismatch.TextureFormatChanged);
        AdvancedTextureSourceMismatchDiagnostics.Note(EAdvancedTextureSourceMismatch.None);

        AdvancedTextureSourceMismatchDiagnostics.GetCount(EAdvancedTextureSourceMismatch.TextureFormatChanged)
            .ShouldBeGreaterThanOrEqualTo(before + 1L);
        AdvancedTextureSourceMismatchDiagnostics.CaptureTotals()
            .ShouldContainKey(nameof(EAdvancedTextureSourceMismatch.TextureFormatChanged));
        System.Text.StringBuilder line = new();
        AdvancedTextureSourceMismatchDiagnostics.AppendTotals(line);
        line.ToString().ShouldContain(" srcMismatch.TextureFormatChanged=");
    }

    private static AdvancedTextureRecord Record(EAdvancedTextureFormatClass format, uint extent, uint mips)
        => new()
        {
            Dimension = EAdvancedTextureDimension.Texture2D,
            Width = extent,
            Height = extent,
            DepthOrLayers = 1u,
            MipCount = mips,
            FormatClass = (uint)format,
            UvScaleBias = new Vector4(1f, 1f, 0f, 0f),
        };

    private static AdvancedSamplerRecord Sampler()
        => new()
        {
            Filter = EAdvancedSamplerFilter.Linear,
            AddressU = EAdvancedSamplerAddressMode.Repeat,
            AddressV = EAdvancedSamplerAddressMode.Repeat,
            LodBiasMinMaxAnisotropy = new Vector4(0f, 0f, 4f, 1f),
        };

    private static XRTexture2D CreateTexture(int mipCount)
    {
        XRTexture2D texture = new()
        {
            Mipmaps = CreateChain(mipCount),
        };
        // Matches the state after an imported resident commit, so the encoder's mip count
        // follows the mip array length.
        CommitResident(texture, mipCount);
        return texture;
    }

    private static void CommitResident(XRTexture2D texture, int mipCount)
    {
        Mipmap2D[] resident = CreateChain(mipCount);
        ImportedTextureResidentPublicationPlan plan = texture.CreateImportedResidentPublicationPlan(
            new TextureStreamingResidentData(resident, 16u, 16u, 16u),
            includeMipChain: true);
        texture.BeginImportedResidentPublication();
        try
        {
            texture.CommitImportedResidentPublication(plan);
        }
        finally
        {
            texture.CompleteImportedResidentPublication();
        }
    }

    // A 16x16 chain with the requested number of levels: 16, 8, 4, 2, 1.
    private static Mipmap2D[] CreateChain(int mipCount)
    {
        Mipmap2D[] chain = new Mipmap2D[mipCount];
        for (int level = 0; level < mipCount; ++level)
        {
            uint extent = Math.Max(16u >> level, 1u);
            chain[level] = new Mipmap2D(extent, extent, new byte[extent * extent * 4u]);
        }
        return chain;
    }

    private static AdvancedGpuResourceBindingSource Encode(XRTexture2D texture)
    {
        AdvancedGpuResourceSourceEncoder.TryEncode(
            texture,
            EAdvancedResourceFallback.Zero,
            out AdvancedGpuResourceBindingSource source,
            out _,
            out string reason).ShouldBeTrue(reason);
        return source;
    }
}
