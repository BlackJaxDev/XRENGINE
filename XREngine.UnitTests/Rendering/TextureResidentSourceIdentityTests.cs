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
public sealed class TextureResidentSourceIdentityTests
{
    private IAssetFileSystem? _previousFiles;
    private IRuntimeFileDiscovery? _previousDiscovery;
    private IRuntimeAssetReadSource? _previousSource;
    private bool _previousCaller;
    private IRuntimeImageCodec? _previousCodec;
    private IDisposable _hostInstallation = null!;
    private TextureSamplingTestHost _host = null!;
    private TextureSamplingTestCodec _codec = null!;
    private string _root = null!;
    private string _authority = null!;
    private int _resolutions;
    private string Original => Path.Combine(_root, "original.png");
    private string Cooked => Path.Combine(_root, "cooked.asset");
    private string Alternate => Path.Combine(_root, "alternate.asset");
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
        RuntimeImageCodecs.Current = _codec = new TextureSamplingTestCodec();
        IRuntimeRenderingHostServices facade = DispatchProxy.Create<IRuntimeRenderingHostServices, TextureSamplingTestHost>();
        _host = (TextureSamplingTestHost)(object)facade;
        _host.Inner = RuntimeRenderingHostServices.Current;
        _hostInstallation = RuntimeRenderingHostServices.Install(facade);
        _root = Path.GetFullPath(Path.Combine("Build", "_AgentValidation", "00000000-000000-shared", "scratch", "texture-authority-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Original, [17, 31]);
        File.SetLastWriteTimeUtc(Original, Timestamp);
        WriteCooked(Cooked, [11, 22, 33, 44]);
        WriteCooked(Alternate, [55, 66, 77, 88]);
        _authority = Cooked;
        _resolutions = 0;
        _host.TextureAuthorityResolver = _ => { _resolutions++; return _authority; };
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
    public void DeferredCacheIdentityUsesTheSameLazySourceAsDecode()
    {
        ITextureStreamingSource source = TextureStreamingSourceFactory.CreateDeferred(Original);
        Assert.That(source.SourcePath, Is.EqualTo(Original));
        Assert.That(_resolutions, Is.Zero);
        Assert.That(TryGet(source, out _, out var key), Is.False);
        Assert.That(key!.Value.AuthorityPath, Is.EqualTo(Cooked));
        Assert.That(_resolutions, Is.EqualTo(1));

        TextureStreamingResidentData data = source.LoadResidentData(1, false, CancellationToken.None);
        AssertPixels(data, [11, 22, 33, 44]);
        TextureStreamingResidentDataReuseCache.Store(source, key, data);
        DisposePixels(data);
        Assert.That(TryGet(source, out var reused, out _), Is.True);
        AssertPixels(reused, [11, 22, 33, 44]);
        DisposePixels(reused);
        Assert.That(_resolutions, Is.EqualTo(1));
        Assert.That(_codec.DecodeCount, Is.Zero);
        Assert.That(source.SourcePath, Is.EqualTo(Original), "The diagnostic source path remains stable.");
    }

    [Test]
    public void DeferredReuseTracksCookedChangesInsteadOfOriginalMetadata()
    {
        ITextureStreamingSource source = TextureStreamingSourceFactory.CreateDeferred(Original);
        DecodeAndStore(source);
        File.SetLastWriteTimeUtc(Original, Timestamp.AddMinutes(1));
        Assert.That(TryGet(source, out var reused, out _), Is.True);
        DisposePixels(reused);
        File.SetLastWriteTimeUtc(Cooked, Timestamp.AddMinutes(1));
        Assert.That(TryGet(source, out _, out var key), Is.False);
        Assert.That(key!.Value.AuthorityPath, Is.EqualTo(Cooked));
    }

    [Test]
    public void DistinctCookedAuthoritiesForOneOriginalDoNotSharePixels()
    {
        ITextureStreamingSource first = TextureStreamingSourceFactory.CreateDeferred(Original);
        DecodeAndStore(first);
        _authority = Alternate;
        ITextureStreamingSource second = TextureStreamingSourceFactory.CreateDeferred(Original);
        Assert.That(TryGet(second, out _, out var key), Is.False);
        Assert.That(key!.Value.AuthorityPath, Is.EqualTo(Alternate));
        TextureStreamingResidentData data = second.LoadResidentData(1, false, CancellationToken.None);
        AssertPixels(data, [55, 66, 77, 88]);
        TextureStreamingResidentDataReuseCache.Store(second, key, data);
        DisposePixels(data);
        Assert.That(TryGet(first, out var firstPixels, out _), Is.True);
        AssertPixels(firstPixels, [11, 22, 33, 44]);
        DisposePixels(firstPixels);
        Assert.That(_resolutions, Is.EqualTo(2));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FallbackTransitionRejectsOldKeyThenReusesTheFallbackFile(bool missing)
    {
        if (missing)
            File.Delete(Cooked);
        else
            File.WriteAllBytes(Cooked, [0]);
        ITextureStreamingSource source = new AssetTextureStreamingSource(Cooked, Original);
        Assert.That(TryGet(source, out _, out var before), Is.False);
        Assert.That(before is null, Is.EqualTo(missing));
        TextureStreamingResidentData data = source.LoadResidentData(1, false, CancellationToken.None);
        AssertPixels(data, [73, 73, 73, 73]);
        Assert.That(source.ResolveCacheSourcePath(), Is.EqualTo(Original));
        long priorBytes = TextureStreamingResidentDataReuseCache.TotalBytes;
        TextureStreamingResidentDataReuseCache.Store(source, before, data);
        Assert.That(TextureStreamingResidentDataReuseCache.TotalBytes, Is.EqualTo(priorBytes));
        DisposePixels(data);

        Assert.That(TryGet(source, out _, out var after), Is.False);
        Assert.That(after!.Value.AuthorityPath, Is.EqualTo(Original));
        data = source.LoadResidentData(1, false, CancellationToken.None);
        TextureStreamingResidentDataReuseCache.Store(source, after, data);
        DisposePixels(data);
        File.WriteAllBytes(Cooked, [0, 1]);
        File.SetLastWriteTimeUtc(Cooked, Timestamp.AddMinutes(1));
        Assert.That(TryGet(source, out var reused, out _), Is.True);
        AssertPixels(reused, [73, 73, 73, 73]);
        DisposePixels(reused);
        File.SetLastWriteTimeUtc(Original, Timestamp.AddMinutes(1));
        Assert.That(TryGet(source, out _, out _), Is.False);
        Assert.That(source.SourcePath, Is.EqualTo(Cooked));
        Assert.That(_codec.DecodeCount, Is.EqualTo(2));
    }

    [Test]
    public void CallerThreadAdmissionRunsBeforeDeferredResolution()
    {
        ITextureStreamingSource source = TextureStreamingSourceFactory.CreateDeferred(Original);
        RuntimeAssetReadServices.IsCallerThread = true;
        Assert.That(TryGet(source, out _, out var key), Is.False);
        Assert.That(key, Is.Null);
        Assert.That(_resolutions, Is.Zero);
    }

    [Test]
    public void ResolutionFailureSkipsCacheAndRemainsADecodeFailure()
    {
        _host.TextureAuthorityResolver = _ => throw new InvalidOperationException("Authority unavailable.");
        ITextureStreamingSource source = TextureStreamingSourceFactory.CreateDeferred(Original);
        Assert.That(TryGet(source, out _, out var key), Is.False);
        Assert.That(key, Is.Null);
        Assert.Throws<InvalidOperationException>(() => source.LoadResidentData(1, false, CancellationToken.None));
    }

    [Test]
    public void SelectionChangeDuringMetadataReadDiscardsTheKey()
    {
        var source = new SwitchingTextureCacheSource(Cooked, Alternate);
        Assert.That(TryGet(source, out _, out var key), Is.False);
        Assert.That(key, Is.Null);
        Assert.That(source.ResolveCalls, Is.EqualTo(2));
    }

    private static bool TryGet(ITextureStreamingSource source, out TextureStreamingResidentData data, out TextureResidentDataCacheKey? key)
        => TextureStreamingResidentDataReuseCache.TryGet(source, 1, false, out data, out key);

    private static void DecodeAndStore(ITextureStreamingSource source)
    {
        Assert.That(TryGet(source, out _, out var key), Is.False);
        TextureStreamingResidentData data = source.LoadResidentData(1, false, CancellationToken.None);
        TextureStreamingResidentDataReuseCache.Store(source, key, data);
        DisposePixels(data);
    }

    private static void WriteCooked(string path, byte[] pixels)
    {
        using RuntimeImage image = new(1, 1, EPixelFormat.Rgba, EPixelType.UnsignedByte, pixels);
        using XRTexture2D texture = new() { Mipmaps = [new Mipmap2D(image)] };
        File.WriteAllBytes(path, XRTexture2D.CreateTextureStreamingPayloadFromTexture(texture));
        File.SetLastWriteTimeUtc(path, Timestamp);
    }

    private static void AssertPixels(TextureStreamingResidentData data, byte[] expected)
        => Assert.That(data.Mipmaps[0].Data!.GetBytes(), Is.EqualTo(expected));

    private static void DisposePixels(TextureStreamingResidentData data)
    {
        foreach (Mipmap2D mip in data.Mipmaps)
            mip.Data = null;
    }
}
