using System.Text;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Imaging;
using XREngine.Rendering;
using YamlDotNet.Serialization;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class TexturePreviewSamplingTests
{
    private IRuntimeImageCodec? _previousCodec;
    private IRuntimeRenderingHostServices _previousHost = null!;

    [SetUp]
    public void SetUp()
    {
        _previousCodec = RuntimeImageCodecs.Current;
        _previousHost = RuntimeRenderingHostServices.Current;
        RuntimeImageCodecs.Current = null;
        RuntimeRenderingHostServices.Reset();
    }

    [TearDown]
    public void TearDown()
    {
        RuntimeImageCodecs.Current = _previousCodec;
        RuntimeRenderingHostServices.Current = _previousHost;
    }

    [TestCase(0, false)]
    [TestCase(1, false)]
    [TestCase(2, false)]
    [TestCase(0, true)]
    [TestCase(1, true)]
    [TestCase(2, true)]
    public void CookedPreviewRetainsSamplingAfterPayloadRelease(int envelope, bool anisotropic)
    {
        using XRTexture2D source = CreateSource();
        source.MinFilter = anisotropic ? ETexMinFilter.LinearMipmapLinear : ETexMinFilter.NearestMipmapNearest;
        source.MaxAnisotropy = anisotropic ? 8 : 1;
        byte[] payload = Encode(source, envelope);
        using XRTexture2D target = new();
        using (CookedPayloadLease lease = CookedPayloadBufferPool.Rent(payload.Length))
        {
            using CookedPayloadOwner owner = lease.TransferToOwner();
            payload.CopyTo(owner.GetSpan());
            Assert.That(XRTexture2D.TryApplyTexturePreviewPayload(owner.Span, target, 4), Is.True);
            owner.GetSpan().Clear();
        }

        AssertPreviewSampling(source, target);
        Assert.That(target.Mipmaps.Length, Is.EqualTo(1));
        Assert.That(target.Width, Is.EqualTo(4));
        Assert.That(target.Height, Is.EqualTo(2));
        Assert.That(target.Mipmaps[0].Data!.GetBytes(), Is.EqualTo(Enumerable.Repeat((byte)34, 32).ToArray()));
        Assert.That(source.Mipmaps.Length, Is.EqualTo(4));
        Assert.That(source.MinLOD, Is.EqualTo(2));
        Assert.That(source.MaxLOD, Is.EqualTo(7));
        Assert.That(target.ImportedSourceMetadataEpoch % 2, Is.Zero);
    }

    [TestCase(ETexMinFilter.Nearest)]
    [TestCase(ETexMinFilter.Linear)]
    [TestCase(ETexMinFilter.NearestMipmapNearest)]
    [TestCase(ETexMinFilter.LinearMipmapNearest)]
    [TestCase(ETexMinFilter.NearestMipmapLinear)]
    [TestCase(ETexMinFilter.LinearMipmapLinear)]
    public void LoadedPreviewKeepsFiltersAndBorrowedSourcePixels(ETexMinFilter filter)
    {
        RuntimeImageCodecs.Current = new TextureSamplingTestCodec();
        using XRTexture2D source = CreateSource();
        source.MinFilter = filter;
        source.MaxAnisotropy = filter == ETexMinFilter.LinearMipmapLinear ? 8 : 1;
        using XRTexture2D target = new()
        {
            Mipmaps = source.Mipmaps.Select(mip => mip.Clone(cloneImage: false)).ToArray(),
            AutoGenerateMipmaps = true,
            Resizable = true,
            LargestMipmapLevel = 2,
            SmallestAllowedMipmapLevel = 3,
        };
        new TextureSamplingSettings(source).ApplyTo(target);
        byte[] originalPixels = source.Mipmaps[0].Data!.GetBytes();

        Assert.That(XRTexture2D.TryPreparePreviewFromLoadedTexture(target, 4), Is.True);
        AssertPreviewSampling(source, target);
        Assert.That(target.Mipmaps.Length, Is.EqualTo(1));
        Assert.That(target.Width, Is.EqualTo(4));
        Assert.That(target.Height, Is.EqualTo(2));
        Assert.That(source.Mipmaps[0].Data!.GetBytes(), Is.EqualTo(originalPixels));
        Assert.That(target.Mipmaps[0].Data, Is.Not.SameAs(source.Mipmaps[0].Data));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void InvalidPreviewLeavesTargetUnchanged(int invalidKind)
    {
        using XRTexture2D invalid = new()
        {
            Mipmaps = invalidKind == 0 ? [] : [new Mipmap2D { Width = 8, Height = 4 }],
        };
        byte[] payload = XRTexture2D.CreateTextureStreamingPayloadFromTexture(invalid);
        if (invalidKind == 2)
            payload = payload[..16];
        using XRTexture2D target = CreateSource();
        Mipmap2D[] originalMips = target.Mipmaps;
        ulong generation = target.CanonicalSourceContentGeneration;

        Assert.That(XRTexture2D.TryApplyTexturePreviewPayload(payload, target, 4), Is.False);
        Assert.That(target.Mipmaps, Is.SameAs(originalMips));
        Assert.That(target.CanonicalSourceContentGeneration, Is.EqualTo(generation));
        Assert.That(target.MinLOD, Is.EqualTo(2));
        Assert.That(target.MaxLOD, Is.EqualTo(7));
        Assert.That(target.ImportedColorSpace, Is.EqualTo(ETextureColorSpace.Srgb));
        Assert.That(target.SamplerName, Is.EqualTo("PreviewSampler"));
    }

    [Test]
    public void LegacyPreviewRestoresLegacySamplingDefaults()
    {
        using Stream stream = typeof(TexturePreviewSamplingTests).Assembly.GetManifestResourceStream("TextureCooking.Legacy")!;
        using MemoryStream bytes = new();
        stream.CopyTo(bytes);
        using XRTexture2D target = CreateSource();
        Assert.That(XRTexture2D.TryApplyTexturePreviewPayload(bytes.ToArray(), target, 2), Is.True);
        Assert.That(target.ImportedColorSpace, Is.EqualTo(ETextureColorSpace.Linear));
        Assert.That(target.ImportedUsage, Is.EqualTo(ETextureImportUsage.Data));
        Assert.That(target.ImportedNormalMapFlipGreen, Is.False);
        Assert.That(target.MaxAnisotropy, Is.EqualTo(1));
        Assert.That(target.EnableComparison, Is.False);
        Assert.That(target.CompareFunc, Is.EqualTo(ETextureCompareFunc.LessOrEqual));
        Assert.That(target.SamplerName, Is.EqualTo("Texture0"));
        Assert.That(target.MinFilter, Is.EqualTo(ETexMinFilter.LinearMipmapLinear));
        Assert.That(target.MinLOD, Is.Zero);
        Assert.That(target.MaxLOD, Is.Zero);
    }

    [Test]
    [TestCase(false)]
    [TestCase(true)]
    public void ResidentPublicationsRestoreAuthoredSamplingAndPreservePromotionBias(bool vulkanPublication)
    {
        using XRTexture2D source = CreateSource();
        source.LodBias = 1.0f;
        byte[] payload = Encode(source, 0);
        Assert.That(XRTexture2D.TryReadResidentDataFromTextureStreamingPayload(payload, 4, false, out var resident), Is.True);
        using XRTexture2D target = new()
        {
            ImportedColorSpace = ETextureColorSpace.Linear,
            MaxAnisotropy = 1,
        };
        ImportedTextureStreamingRecord streamingRecord = GetManagerStreamingRecord(target);
        ApplyResidentPublication(target, resident, includeMipChain: false, vulkanPublication: vulkanPublication);
        AssertResidentSampling(source, target);
        Assert.That(target.LodBias, Is.EqualTo(1.0f));

        // A completed promotion has a temporary +2 bias. The next resident
        // publication changes authored bias from 1 to 4 and must carry the record's
        // fade delta forward so the real completion lifecycle restores base bias 4.
        target.LodBias = source.LodBias + 2.0f;
        lock (streamingRecord.Sync)
        {
            streamingRecord.ResidentMaxDimension = 8u;
            streamingRecord.SourceWidth = 8u;
            streamingRecord.SourceHeight = 4u;
            streamingRecord.BaseLodBias = 1.0f;
            streamingRecord.CurrentStreamingLodBias = 2.0f;
            streamingRecord.PromotionFadeStartBias = 2.0f;
            streamingRecord.PromotionFadeStartFrameId = 1L;
            streamingRecord.PromotionFadeEndFrameId = 10L;
        }
        using XRTexture2D promotedSource = CreateSource();
        promotedSource.SamplerName = "PromotedSampler";
        promotedSource.MinFilter = ETexMinFilter.NearestMipmapNearest;
        promotedSource.MagFilter = ETexMagFilter.Nearest;
        promotedSource.UWrap = ETexWrapMode.Repeat;
        promotedSource.VWrap = ETexWrapMode.ClampToBorder;
        promotedSource.MinLOD = 0;
        promotedSource.MaxLOD = 3;
        promotedSource.LodBias = 4.0f;
        promotedSource.MaxAnisotropy = 16.0f;
        promotedSource.EnableComparison = false;
        promotedSource.CompareFunc = ETextureCompareFunc.Greater;
        promotedSource.ImportedColorSpace = ETextureColorSpace.Linear;
        promotedSource.ImportedUsage = ETextureImportUsage.Data;
        promotedSource.ImportedNormalMapFlipGreen = false;
        promotedSource.AlphaAsTransparency = true;
        byte[] promotedPayload = Encode(promotedSource, 0);
        Assert.That(XRTexture2D.TryReadResidentDataFromTextureStreamingPayload(promotedPayload, 8, true, out var promotedResident), Is.True);

        ApplyResidentPublication(target, promotedResident, includeMipChain: true, vulkanPublication: vulkanPublication);

        AssertResidentSampling(promotedSource, target);
        Assert.That(target.LodBias, Is.EqualTo(6.0f));
        Assert.That(target.LodBias - 2.0f, Is.EqualTo(promotedSource.LodBias));

        CompleteSameSizeTransition(streamingRecord, target, completedSize: 8u, frameId: 10L);
        Assert.That(target.LodBias, Is.EqualTo(4.0f));
        Assert.That(streamingRecord.CurrentStreamingLodBias, Is.Zero);
        Assert.That(streamingRecord.BaseLodBias, Is.EqualTo(4.0f));
    }

    [Test]
    public void RawResidentPublicationUsesLiveSamplingEditsAndRestoresPreviewLodRange()
    {
        using XRTexture2D authoredSource = CreateSource();
        using XRTexture2D target = new();
        TextureStreamingResidentData previewData = new(
            [authoredSource.Mipmaps[0].Clone(cloneImage: true)],
            authoredSource.Width,
            authoredSource.Height,
            Math.Max(authoredSource.Width, authoredSource.Height));
        XRTexture2D.ApplyResidentData(
            target,
            previewData,
            includeMipChain: false,
            previewSampling: new TextureSamplingSettings(authoredSource));

        target.SamplerName = "RawLiveEdit";
        target.MinFilter = ETexMinFilter.Nearest;
        target.MagFilter = ETexMagFilter.Nearest;
        target.UWrap = ETexWrapMode.Repeat;
        target.VWrap = ETexWrapMode.ClampToBorder;
        target.LodBias = 5.0f;
        target.MaxAnisotropy = 2.0f;
        target.EnableComparison = false;
        target.CompareFunc = ETextureCompareFunc.Greater;
        target.ImportedColorSpace = ETextureColorSpace.Linear;
        target.ImportedUsage = ETextureImportUsage.Data;
        target.ImportedNormalMapFlipGreen = false;
        target.AlphaAsTransparency = true;

        TextureStreamingResidentData residentData = new(
            authoredSource.Mipmaps.Select(mip => mip.Clone(cloneImage: true)).ToArray(),
            authoredSource.Width,
            authoredSource.Height,
            Math.Max(authoredSource.Width, authoredSource.Height));
        XRTexture2D.ApplyResidentData(target, residentData, includeMipChain: true);

        Assert.That(target.SamplerName, Is.EqualTo("RawLiveEdit"));
        Assert.That(target.MinFilter, Is.EqualTo(ETexMinFilter.Nearest));
        Assert.That(target.MagFilter, Is.EqualTo(ETexMagFilter.Nearest));
        Assert.That(target.UWrap, Is.EqualTo(ETexWrapMode.Repeat));
        Assert.That(target.VWrap, Is.EqualTo(ETexWrapMode.ClampToBorder));
        Assert.That(target.MinLOD, Is.EqualTo(authoredSource.MinLOD));
        Assert.That(target.MaxLOD, Is.EqualTo(authoredSource.MaxLOD));
        Assert.That(target.LodBias, Is.EqualTo(5.0f));
        Assert.That(target.MaxAnisotropy, Is.EqualTo(2.0f));
        Assert.That(target.EnableComparison, Is.False);
        Assert.That(target.CompareFunc, Is.EqualTo(ETextureCompareFunc.Greater));
        Assert.That(target.ImportedColorSpace, Is.EqualTo(ETextureColorSpace.Linear));
        Assert.That(target.ImportedUsage, Is.EqualTo(ETextureImportUsage.Data));
        Assert.That(target.ImportedNormalMapFlipGreen, Is.False);
        Assert.That(target.AlphaAsTransparency, Is.True);
    }

    [Test]
    public void ChangedAuthorityDoesNotRestorePreviousPreviewSamplingSnapshot()
    {
        using XRTexture2D authoredSource = CreateSource();
        using XRTexture2D target = new() { FilePath = "old-source.png" };
        TextureStreamingResidentData previewData = new(
            [authoredSource.Mipmaps[0].Clone(cloneImage: true)],
            authoredSource.Width,
            authoredSource.Height,
            Math.Max(authoredSource.Width, authoredSource.Height));
        XRTexture2D.ApplyResidentData(
            target,
            previewData,
            includeMipChain: false,
            previewSampling: new TextureSamplingSettings(authoredSource));

        target.FilePath = "new-source.png";
        target.MinLOD = 3;
        target.MaxLOD = 4;
        TextureStreamingResidentData residentData = new(
            authoredSource.Mipmaps.Select(mip => mip.Clone(cloneImage: true)).ToArray(),
            authoredSource.Width,
            authoredSource.Height,
            Math.Max(authoredSource.Width, authoredSource.Height));

        XRTexture2D.ApplyResidentData(target, residentData, includeMipChain: true);

        Assert.That(target.MinLOD, Is.EqualTo(3));
        Assert.That(target.MaxLOD, Is.EqualTo(4));
    }

    private static ImportedTextureStreamingRecord GetManagerStreamingRecord(XRTexture2D texture)
    {
        ImportedTextureStreamingManager manager = ImportedTextureStreamingManager.Instance;
        FieldInfo registryField = typeof(ImportedTextureStreamingManager).GetField(
            "_registry",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        TextureStreamingRegistry registry = (TextureStreamingRegistry)registryField.GetValue(manager)!;
        return registry.GetOrCreateRecord(texture, null, _ => throw new InvalidOperationException("No backend is needed for this test."));
    }

    private static void CompleteSameSizeTransition(
        ImportedTextureStreamingRecord record,
        XRTexture2D texture,
        uint completedSize,
        long frameId)
    {
        CancellationTokenSource cts = new();
        lock (record.Sync)
        {
            record.PendingLoadCts = cts;
            record.PendingMaxDimension = completedSize;
        }

        MethodInfo clearPendingTransition = typeof(ImportedTextureStreamingManager).GetMethod(
            "ClearPendingTransition",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        clearPendingTransition.Invoke(null,
        [
            record,
            cts,
            texture,
            completedSize,
            frameId,
            false,
            null,
            false,
        ]);
    }

    private static void ApplyResidentPublication(
        XRTexture2D target,
        TextureStreamingResidentData residentData,
        bool includeMipChain,
        bool vulkanPublication)
    {
        if (vulkanPublication)
            XRTexture2D.ApplyResidentDataForVulkanPublication(target, residentData, includeMipChain);
        else
            XRTexture2D.ApplyResidentData(target, residentData, includeMipChain);
    }

    private static byte[] Encode(XRTexture2D source, int envelope)
        => envelope switch
        {
            0 => XRTexture2D.CreateTextureStreamingPayloadFromTexture(source),
            1 => RuntimeCookedBinarySerializer.Serialize(source),
            2 => Encoding.UTF8.GetBytes(new SerializerBuilder()
                .WithTypeConverter(new XRTexture2DYamlTypeConverter())
                .WithTypeConverter(new DataSourceYamlTypeConverter()).Build().Serialize(source)),
            _ => throw new ArgumentOutOfRangeException(nameof(envelope)),
        };

    private static XRTexture2D CreateSource()
    {
        Mipmap2D[] mips = new Mipmap2D[4];
        for (int i = 0; i < mips.Length; i++)
        {
            uint width = 8u >> i;
            uint height = Math.Max(1u, 4u >> i);
            byte[] pixels = new byte[width * height * 4];
            Array.Fill(pixels, (byte)(17 * (i + 1)));
            using RuntimeImage image = new(width, height, EPixelFormat.Rgba, EPixelType.UnsignedByte, pixels);
            mips[i] = new Mipmap2D(image);
        }
        return new XRTexture2D
        {
            Mipmaps = mips,
            SamplerName = "PreviewSampler",
            MinFilter = ETexMinFilter.LinearMipmapLinear,
            MagFilter = ETexMagFilter.Linear,
            UWrap = ETexWrapMode.ClampToEdge,
            VWrap = ETexWrapMode.MirroredRepeat,
            MinLOD = 2,
            MaxLOD = 7,
            MaxAnisotropy = 8,
            EnableComparison = true,
            CompareFunc = ETextureCompareFunc.LessOrEqual,
            ImportedColorSpace = ETextureColorSpace.Srgb,
            ImportedUsage = ETextureImportUsage.Normal,
            ImportedNormalMapFlipGreen = true,
            AlphaAsTransparency = false,
        };
    }

    private static void AssertPreviewSampling(XRTexture2D source, XRTexture2D target)
    {
        Assert.That(target.SamplerName, Is.EqualTo(source.SamplerName));
        Assert.That(target.MinFilter, Is.EqualTo(source.MinFilter));
        Assert.That(target.MagFilter, Is.EqualTo(source.MagFilter));
        Assert.That(target.UWrap, Is.EqualTo(source.UWrap));
        Assert.That(target.VWrap, Is.EqualTo(source.VWrap));
        Assert.That(target.MaxAnisotropy, Is.EqualTo(source.MaxAnisotropy));
        Assert.That(target.LodBias, Is.EqualTo(source.LodBias));
        Assert.That(target.EnableComparison, Is.EqualTo(source.EnableComparison));
        Assert.That(target.CompareFunc, Is.EqualTo(source.CompareFunc));
        Assert.That(target.ImportedColorSpace, Is.EqualTo(source.ImportedColorSpace));
        Assert.That(target.ImportedUsage, Is.EqualTo(source.ImportedUsage));
        Assert.That(target.ImportedNormalMapFlipGreen, Is.EqualTo(source.ImportedNormalMapFlipGreen));
        Assert.That(target.AlphaAsTransparency, Is.EqualTo(source.AlphaAsTransparency));
        Assert.That(target.MinLOD, Is.Zero);
        Assert.That(target.MaxLOD, Is.Zero);
        Assert.That(target.LargestMipmapLevel, Is.Zero);
        Assert.That(target.SmallestAllowedMipmapLevel, Is.Zero);
        Assert.That(target.AutoGenerateMipmaps, Is.False);
        Assert.That(target.Resizable, Is.False);
        Assert.That(target.SizedInternalFormat, Is.EqualTo(ESizedInternalFormat.Rgba8));
    }

    private static void AssertResidentSampling(XRTexture2D source, XRTexture2D target)
    {
        Assert.That(target.SamplerName, Is.EqualTo(source.SamplerName));
        Assert.That(target.MinFilter, Is.EqualTo(source.MinFilter));
        Assert.That(target.MagFilter, Is.EqualTo(source.MagFilter));
        Assert.That(target.UWrap, Is.EqualTo(source.UWrap));
        Assert.That(target.VWrap, Is.EqualTo(source.VWrap));
        Assert.That(target.MinLOD, Is.EqualTo(source.MinLOD));
        Assert.That(target.MaxLOD, Is.EqualTo(source.MaxLOD));
        // LodBias may also contain the active, temporary promotion-fade offset.
        Assert.That(target.MaxAnisotropy, Is.EqualTo(source.MaxAnisotropy));
        Assert.That(target.EnableComparison, Is.EqualTo(source.EnableComparison));
        Assert.That(target.CompareFunc, Is.EqualTo(source.CompareFunc));
        Assert.That(target.ImportedColorSpace, Is.EqualTo(source.ImportedColorSpace));
        Assert.That(target.ImportedUsage, Is.EqualTo(source.ImportedUsage));
        Assert.That(target.ImportedNormalMapFlipGreen, Is.EqualTo(source.ImportedNormalMapFlipGreen));
        Assert.That(target.AlphaAsTransparency, Is.EqualTo(source.AlphaAsTransparency));
    }
}
