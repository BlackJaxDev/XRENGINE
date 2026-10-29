using System.Reflection;
using NUnit.Framework;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Imaging;
using XREngine.Rendering;
using XREngine.Runtime.Platform.Desktop;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class TextureCacheSamplingTests
{
    private IAssetFileSystem? _previousFiles;
    private IRuntimeFileDiscovery? _previousDiscovery;
    private IRuntimeAssetReadSource? _previousSource;
    private bool _previousCaller;
    private IRuntimeImageCodec? _previousCodec;
    private IDisposable _hostInstallation = null!;
    private TextureSamplingTestHost _host = null!;
    private TextureSamplingTestCodec _imageCodec = null!;
    private string _root = null!;
    private string SourcePath => Path.Combine(_root, "source.png");
    private string CachePath => Path.Combine(_root, "cache.asset");
    private static readonly DateTime Timestamp = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    [SetUp]
    public void SetUp()
    {
        _previousFiles = AssetFileSystemServices.Current;
        _previousDiscovery = RuntimeFileDiscoveryServices.Current;
        _previousSource = RuntimeAssetReadServices.Source;
        _previousCaller = RuntimeAssetReadServices.IsCallerThread;
        _previousCodec = RuntimeImageCodecs.Current;
        RuntimeAssetReadServices.SetSource(null);
        RuntimeAssetReadServices.IsCallerThread = false;
        AssetFileSystemServices.Current = new DesktopAssetFileSystem();
        RuntimeImageCodecs.Current = _imageCodec = new TextureSamplingTestCodec();
        IRuntimeRenderingHostServices facade = DispatchProxy.Create<IRuntimeRenderingHostServices, TextureSamplingTestHost>();
        _host = (TextureSamplingTestHost)(object)facade;
        _host.Inner = RuntimeRenderingHostServices.Current;
        _hostInstallation = RuntimeRenderingHostServices.Install(facade);
        _root = Path.GetFullPath(Path.Combine("Build", "_AgentValidation", "00000000-000000-shared", "scratch", "texture-sampling-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(SourcePath, [17, 31]);
    }

    [TearDown]
    public void TearDown()
    {
        _hostInstallation.Dispose();
        RuntimeImageCodecs.Current = _previousCodec;
        AssetFileSystemServices.Current = _previousFiles;
        RuntimeFileDiscoveryServices.Current = _previousDiscovery;
        RuntimeAssetReadServices.SetSource(_previousSource);
        RuntimeAssetReadServices.IsCallerThread = _previousCaller;
        Directory.Delete(_root, true);
    }

    [Test]
    public void CachePreparationKeepsSamplingButRebuildsStorage()
    {
        using XRTexture2D source = CreateSource();
        TextureStreamingCacheCodec codec = new();
        using XRTexture2D cache = (XRTexture2D)codec.PrepareForWrite(CachePath, source);
        Assert.That(cache, Is.Not.SameAs(source));
        AssertSampling(cache);
        Assert.That(cache.FilePath, Is.EqualTo(CachePath));
        Assert.That(cache.OriginalPath, Is.EqualTo(SourcePath));
        Assert.That(cache.OriginalLastWriteTimeUtc, Is.EqualTo(Timestamp));
        Assert.That(cache.AutoGenerateMipmaps, Is.False);
        Assert.That(cache.Resizable, Is.False);
        Assert.That(cache.SizedInternalFormat, Is.EqualTo(ESizedInternalFormat.Rgba8));
        Assert.That(cache.LargestMipmapLevel, Is.Zero);
        Assert.That(cache.SmallestAllowedMipmapLevel, Is.EqualTo(1000));
        Assert.That(cache.Mipmaps.Length, Is.EqualTo(8));
        Assert.That(cache.Mipmaps[0].Width, Is.EqualTo(128));
        Assert.That(cache.Mipmaps[^1].Width, Is.EqualTo(1));
        Assert.That(cache.Mipmaps[^1].Data!.GetBytes(), Is.EqualTo(new byte[] { 73, 73, 73, 73 }));
        Assert.That(_imageCodec.DecodeCount, Is.EqualTo(1));
        Assert.That(source.AutoGenerateMipmaps, Is.True);
        Assert.That(source.Mipmaps.Length, Is.EqualTo(1));
        Assert.That(source.LargestMipmapLevel, Is.EqualTo(2));

        byte[] payload = XRTexture2D.CreateTextureStreamingPayloadFromTexture(cache);
        Assert.That(XRTexture2D.TryDeserializeTextureStreamingPayload(payload, out XRTexture2D? restored), Is.True);
        using (restored)
        {
            AssertSampling(restored!);
            Assert.That(restored!.Mipmaps.Length, Is.EqualTo(8));
        }
    }

    [Test]
    public void PathOnlyCacheKeepsImportDefaults()
    {
        Assert.That(XRTexture2D.TryCreateTextureStreamingCacheAsset(SourcePath, CachePath, Timestamp, out XRTexture2D cache), Is.True);
        using (cache)
        {
            Assert.That(cache.MinFilter, Is.EqualTo(ETexMinFilter.LinearMipmapLinear));
            Assert.That(cache.UWrap, Is.EqualTo(ETexWrapMode.Repeat));
            Assert.That(cache.ImportedColorSpace, Is.EqualTo(ETextureColorSpace.Linear));
            Assert.That(cache.ImportedUsage, Is.EqualTo(ETextureImportUsage.Data));
            Assert.That(cache.MaxAnisotropy, Is.EqualTo(1));
            Assert.That(cache.EnableComparison, Is.False);
            Assert.That(cache.AlphaAsTransparency, Is.True);
        }
    }

    [Test]
    public void StreamableTextureKeepsItsStorageAndSkipsDecode()
    {
        using XRTexture2D source = CreateSource();
        using RuntimeImage tail = TextureSamplingTestCodec.CreateImage(1, 1);
        source.Mipmaps = [source.Mipmaps[0], new Mipmap2D(tail)];
        Assert.That(new TextureStreamingCacheCodec().PrepareForWrite(CachePath, source), Is.SameAs(source));
        Assert.That(_imageCodec.DecodeCount, Is.Zero);
        AssertSampling(source);
    }

    [Test]
    public void MissingSourceDoesNotReturnASamplingOnlyCache()
    {
        using XRTexture2D source = CreateSource();
        File.Delete(SourcePath);
        Assert.That(XRTexture2D.TryCreateTextureStreamingCacheAsset(source, SourcePath, CachePath, Timestamp, out XRTexture2D cache), Is.False);
        using (cache)
            Assert.That(cache.SamplerName, Is.Null);
        Assert.That(new TextureStreamingCacheCodec().PrepareForWrite(CachePath, source), Is.SameAs(source));
        Assert.That(_imageCodec.DecodeCount, Is.Zero);
    }

    [Test]
    public void TextureAssetCopyKeepsComparisonAndBindingSettings()
    {
        using XRTexture2D source = CreateSource();
        _host.LoadedTexture = source;
        using XRTexture2D target = new();
        Assert.That(target.Load3rdParty(CachePath), Is.True);
        AssertSampling(target);
        Assert.That(target.Mipmaps[0].Data, Is.Not.SameAs(source.Mipmaps[0].Data));
        Assert.That(target.Mipmaps[0].Data!.GetBytes(), Is.EqualTo(source.Mipmaps[0].Data!.GetBytes()));
    }

    [Test]
    public void CacheVariantSeparatesOldSamplingAndRetainsExplicitVariant()
    {
        TextureStreamingCacheCodec codec = new();
        string key = codec.ResolveDefaultVariantKey(null)!;
        Assert.That(key, Does.StartWith("TextureStreaming_v4_preview"));
        Assert.That(codec.ResolveDefaultVariantKey("normal"), Is.EqualTo(Path.Combine(key, "normal")));
        Assert.That(codec.ResolveDefaultVariantKey(" "), Is.EqualTo(key));
    }

    private XRTexture2D CreateSource()
    {
        using RuntimeImage image = TextureSamplingTestCodec.CreateImage(128, 64);
        return new XRTexture2D
        {
            OriginalPath = SourcePath,
            OriginalLastWriteTimeUtc = Timestamp,
            Mipmaps = [new Mipmap2D(image)],
            AutoGenerateMipmaps = true,
            Resizable = true,
            SizedInternalFormat = ESizedInternalFormat.Srgb8Alpha8,
            LargestMipmapLevel = 2,
            SmallestAllowedMipmapLevel = 3,
            SamplerName = "AuthoredSampler",
            MinFilter = ETexMinFilter.NearestMipmapNearest,
            MagFilter = ETexMagFilter.Nearest,
            UWrap = ETexWrapMode.ClampToEdge,
            VWrap = ETexWrapMode.MirroredRepeat,
            MinLOD = 2,
            MaxLOD = 3,
            LodBias = 0.75f,
            MaxAnisotropy = 8,
            EnableComparison = true,
            CompareFunc = ETextureCompareFunc.Greater,
            ImportedColorSpace = ETextureColorSpace.Srgb,
            ImportedUsage = ETextureImportUsage.Normal,
            ImportedNormalMapFlipGreen = true,
            AlphaAsTransparency = false,
        };
    }

    private static void AssertSampling(XRTexture2D texture)
    {
        Assert.That(texture.SamplerName, Is.EqualTo("AuthoredSampler"));
        Assert.That(texture.MinFilter, Is.EqualTo(ETexMinFilter.NearestMipmapNearest));
        Assert.That(texture.MagFilter, Is.EqualTo(ETexMagFilter.Nearest));
        Assert.That(texture.UWrap, Is.EqualTo(ETexWrapMode.ClampToEdge));
        Assert.That(texture.VWrap, Is.EqualTo(ETexWrapMode.MirroredRepeat));
        Assert.That(texture.MinLOD, Is.EqualTo(2));
        Assert.That(texture.MaxLOD, Is.EqualTo(3));
        Assert.That(texture.LodBias, Is.EqualTo(0.75f));
        Assert.That(texture.MaxAnisotropy, Is.EqualTo(8));
        Assert.That(texture.EnableComparison, Is.True);
        Assert.That(texture.CompareFunc, Is.EqualTo(ETextureCompareFunc.Greater));
        Assert.That(texture.ImportedColorSpace, Is.EqualTo(ETextureColorSpace.Srgb));
        Assert.That(texture.ImportedUsage, Is.EqualTo(ETextureImportUsage.Normal));
        Assert.That(texture.ImportedNormalMapFlipGreen, Is.True);
        Assert.That(texture.AlphaAsTransparency, Is.False);
    }
}
