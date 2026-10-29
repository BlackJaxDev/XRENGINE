using NUnit.Framework;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Imaging;
using XREngine.Rendering;
using XREngine.UnitTests.Core;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class TextureResidentCacheMetadataTests
{
    private IRuntimeFileDiscovery? _previousFiles;
    private IRuntimeAssetReadSource? _previousSource;
    private IRuntimeRenderingHostServices _previousHost = null!;
    private bool _previousCaller;
    private TextureMetadataTestFiles _files = null!;
    private ITextureStreamingSource _source = null!;

    [SetUp]
    public void SetUp()
    {
        _previousFiles = RuntimeFileDiscoveryServices.Current;
        _previousSource = RuntimeAssetReadServices.Source;
        _previousCaller = RuntimeAssetReadServices.IsCallerThread;
        _previousHost = RuntimeRenderingHostServices.Current;
        RuntimeAssetReadServices.SetSource(null);
        RuntimeAssetReadServices.IsCallerThread = false;
        RuntimeRenderingHostServices.Reset();
        RuntimeFileDiscoveryServices.Current = _files = new TextureMetadataTestFiles();
        string path = Path.GetFullPath(Path.Combine("Build", "_AgentValidation", "00000000-000000-shared", "scratch", "virtual-texture-" + Guid.NewGuid().ToString("N")));
        _source = new AssetTextureStreamingSource(path);
    }

    [TearDown]
    public void TearDown()
    {
        RuntimeFileDiscoveryServices.Current = _previousFiles;
        RuntimeAssetReadServices.SetSource(_previousSource);
        RuntimeAssetReadServices.IsCallerThread = _previousCaller;
        RuntimeRenderingHostServices.Current = _previousHost;
    }

    [Test]
    public void VirtualMetadataSupportsReuseWithIndependentPixelOwnership()
    {
        Assert.That(File.Exists(_source.SourcePath), Is.False);
        TextureResidentDataCacheKey? key = CaptureMiss();
        TextureStreamingResidentData data = CreateData();
        TextureStreamingResidentDataReuseCache.Store(_source, key, data);
        DisposePixels(data);

        Assert.That(TryGet(out var first, out _), Is.True);
        Assert.That(first.Mipmaps[0].Data!.GetBytes(), Is.EqualTo(new byte[] { 11, 22, 33, 44 }));
        DisposePixels(first);
        Assert.That(TryGet(out var second, out _), Is.True);
        Assert.That(second.Mipmaps[0].Data!.GetBytes(), Is.EqualTo(new byte[] { 11, 22, 33, 44 }));
        DisposePixels(second);
        Assert.That(_files.Calls, Is.EqualTo(4));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void RequestShapeRemainsPartOfIdentity(bool changeDimension)
    {
        StoreCurrent();
        Assert.That(TextureStreamingResidentDataReuseCache.TryGet(_source,
            changeDimension ? 2u : 1u, !changeDimension, out _, out var key), Is.False);
        Assert.That(key, Is.Not.Null);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void MetadataChangesInvalidateHits(bool changeTimestamp)
    {
        StoreCurrent();
        if (changeTimestamp)
            _files.TimestampTicks++;
        else
            _files.Length++;
        Assert.That(TryGet(out _, out var key), Is.False);
        Assert.That(key, Is.Not.Null);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ProviderReinstallationInvalidatesHits(bool sameInstance)
    {
        StoreCurrent();
        RuntimeFileDiscoveryServices.Current = sameInstance ? _files : new TextureMetadataTestFiles();
        Assert.That(TryGet(out _, out var key), Is.False);
        Assert.That(key, Is.Not.Null);
    }

    [Test]
    public void ReplacementDuringMetadataLookupCannotProduceADecodeKey()
    {
        _files.OnProbe = () => RuntimeFileDiscoveryServices.Current = new TextureMetadataTestFiles();
        Assert.That(TryGet(out _, out var key), Is.False);
        Assert.That(key, Is.Null);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    public void StoreRejectsMetadataOrProviderChangesDuringDecode(int change)
    {
        TextureResidentDataCacheKey? key = CaptureMiss();
        TextureStreamingResidentData data = CreateData();
        if (change == 0)
            _files.TimestampTicks++;
        else if (change == 1)
            _files.Length++;
        else if (change == 2)
            RuntimeFileDiscoveryServices.Current = new TextureMetadataTestFiles();
        else if (change == 3)
            RuntimeFileDiscoveryServices.Current = _files;
        else
            _source = new AssetTextureStreamingSource(_source.SourcePath + "-replacement");

        long previousBytes = TextureStreamingResidentDataReuseCache.TotalBytes;
        TextureStreamingResidentDataReuseCache.Store(_source, key, data);
        Assert.That(TextureStreamingResidentDataReuseCache.TotalBytes, Is.EqualTo(previousBytes));
        Assert.That(TryGet(out _, out _), Is.False);
        Assert.That(data.Mipmaps[0].Data!.GetBytes(), Is.EqualTo(new byte[] { 11, 22, 33, 44 }));
        DisposePixels(data);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    public void UnavailableMetadataSkipsReuseAndCannotBeAcquiredAfterDecode(int failure)
    {
        switch (failure)
        {
            case 0: _files.ExistsResult = false; break;
            case 1: _files.Length = -1; break;
            case 2: _files.TimestampTicks = -1; break;
            case 3: _files.Failure = new IOException("Metadata unavailable."); break;
            case 4: RuntimeFileDiscoveryServices.Current = null; break;
            case 5: RuntimeFileDiscoveryServices.Current = new AssetDirectoryTestFileSystem(); break;
        }
        Assert.That(TryGet(out _, out var key), Is.False);
        Assert.That(key, Is.Null);

        RuntimeFileDiscoveryServices.Current = new TextureMetadataTestFiles();
        TextureStreamingResidentData data = CreateData();
        long previousBytes = TextureStreamingResidentDataReuseCache.TotalBytes;
        TextureStreamingResidentDataReuseCache.Store(_source, key, data);
        Assert.That(TextureStreamingResidentDataReuseCache.TotalBytes, Is.EqualTo(previousBytes));
        Assert.That(TryGet(out _, out _), Is.False);
        DisposePixels(data);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void HostAdmissionRejectsMetadataBeforeProviderUse(int mode)
    {
        if (mode == 0)
            RuntimeAssetReadServices.IsCallerThread = true;
        else
        {
            _files.SupportsSynchronousReads = mode != 1;
            _files.SupportsHostFileAccess = mode != 2;
            RuntimeAssetReadServices.SetSource(_files);
        }
        Assert.That(TryGet(out _, out var key), Is.False);
        Assert.That(key, Is.Null);
        Assert.That(_files.Calls, Is.Zero);
    }

    [Test]
    public void InvalidPathIsACacheMiss()
    {
        _source = new AssetTextureStreamingSource("invalid\0path");
        Assert.That(TryGet(out _, out var key), Is.False);
        Assert.That(key, Is.Null);
        Assert.That(_files.Calls, Is.Zero);
    }

    private bool TryGet(out TextureStreamingResidentData data, out TextureResidentDataCacheKey? key)
        => TextureStreamingResidentDataReuseCache.TryGet(_source, 1, false, out data, out key);

    private TextureResidentDataCacheKey? CaptureMiss()
    {
        Assert.That(TryGet(out _, out var key), Is.False);
        Assert.That(key, Is.Not.Null);
        return key;
    }

    private void StoreCurrent()
    {
        TextureResidentDataCacheKey? key = CaptureMiss();
        TextureStreamingResidentData data = CreateData();
        TextureStreamingResidentDataReuseCache.Store(_source, key, data);
        DisposePixels(data);
    }

    private static TextureStreamingResidentData CreateData()
    {
        using RuntimeImage image = new(1, 1, EPixelFormat.Rgba, EPixelType.UnsignedByte, new byte[] { 11, 22, 33, 44 });
        return new TextureStreamingResidentData([new Mipmap2D(image)], 1, 1, 1);
    }

    private static void DisposePixels(TextureStreamingResidentData data)
    {
        foreach (Mipmap2D mip in data.Mipmaps)
            mip.Data = null;
    }
}
