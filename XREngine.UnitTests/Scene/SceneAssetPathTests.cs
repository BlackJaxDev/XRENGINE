using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using NUnit.Framework;
using XREngine.Core.Files;
using XREngine.Data;

namespace XREngine.UnitTests.Scene;

[TestFixture, NonParallelizable]
public sealed class SceneAssetPathTests
{
    private IRuntimeFileDiscovery? _previousFiles;
    private IRuntimeAssetReadSource? _previousSource;
    private bool _previousCallerThread;
    private string _root = null!;
    private ProbeFiles _files = null!;

    [SetUp]
    public void SetUp()
    {
        _previousFiles = RuntimeFileDiscoveryServices.Current;
        _previousSource = RuntimeAssetReadServices.Source;
        _previousCallerThread = RuntimeAssetReadServices.IsCallerThread;
        RuntimeAssetReadServices.SetSource(null);
        RuntimeAssetReadServices.IsCallerThread = false;
        _root = Path.Combine(Path.GetTempPath(), "scene-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _files = new ProbeFiles();
        RuntimeFileDiscoveryServices.Current = _files;
    }

    [TearDown]
    public void TearDown()
    {
        RuntimeAssetReadServices.IsCallerThread = _previousCallerThread;
        RuntimeAssetReadServices.SetSource(_previousSource);
        RuntimeFileDiscoveryServices.Current = _previousFiles;
        Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void HostResolutionKeepsExistingPathAndExtensionFallback()
    {
        string path = Path.Combine(_root, "scene");
        _files.Result = true;
        Assert.That(ResolveHost("scene"), Is.EqualTo(path));
        _files.Result = false;
        Assert.That(ResolveHost("scene"), Is.EqualTo(path + ".asset"));
        Assert.That(ResolveHost("scene.yaml"), Is.EqualTo(path + ".yaml"));
        Assert.That(_files.Calls, Is.EqualTo(3));
    }

    [Test]
    public void MissingProviderReportsMissingReadCapability()
    {
        RuntimeFileDiscoveryServices.Current = null;
        File.WriteAllText(Path.Combine(_root, "scene"), "test");
        Assert.That(Assert.Throws<NotSupportedException>(() => ResolveHost("scene"))!.Message,
            Does.Contain("AssetSource.HostFileReadUnavailable"));
        Assert.Throws<NotSupportedException>(() => ResolveHost("missing"));
    }

    [Test]
    public void CallerThreadRejectsRelativeHostProbe()
    {
        RuntimeAssetReadServices.IsCallerThread = true;
        Assert.Throws<NotSupportedException>(() => ResolveHost("scene"));
        Assert.That(_files.Calls, Is.Zero);
    }

    [Test]
    public void DiscoveryReplacementRejectsProbeResult()
    {
        _files.OnProbe = () => RuntimeFileDiscoveryServices.Current = new ProbeFiles();
        Assert.Throws<OperationCanceledException>(() => ResolveHost("scene"));
    }

    [Test]
    public void SourceReplacementRejectsProbeResult()
    {
        _files.OnProbe = () => DirectStorageIO.Source = new Catalog();
        Assert.Throws<OperationCanceledException>(() => ResolveHost("scene"));
    }

    [Test]
    public void CatalogIdentityUsesMetadataOnCallerThread()
    {
        DirectStorageIO.Source = new Catalog();
        RuntimeAssetReadServices.IsCallerThread = true;
        Assert.That(ResolveScene("scenes/start"), Is.EqualTo("/game/scenes/start.asset"));
        Assert.That(_files.Calls, Is.Zero);
    }

    [Test]
    public void RootedPathDoesNotProbeHostFiles()
    {
        RuntimeAssetReadServices.IsCallerThread = true;
        string path = Path.Combine(_root, "scene.asset");
        Assert.That(ResolveScene(path), Is.EqualTo(path));
        Assert.That(_files.Calls, Is.Zero);
    }

    private string ResolveHost(string input)
        => EngineRuntimeSceneStreamingHostServices.ResolveHostSceneAssetPath(input, _root);

    private static string? ResolveScene(string input)
    {
        MethodInfo method = typeof(EngineRuntimeSceneStreamingHostServices).GetMethod("ResolveSceneAssetPath", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (string?)method.Invoke(null, [input]);
    }

    private sealed class ProbeFiles : IRuntimeFileDiscovery, IRuntimeHostFileReadBackend
    {
        public bool Result;
        public int Calls;
        public Action? OnProbe;
        public bool FileExists(string path) { Calls++; OnProbe?.Invoke(); return Result; }
        public Stream OpenRead(string path, FileShare share) => throw new NotSupportedException();
        public byte[] ReadAllBytes(string path) => throw new NotSupportedException();
        public IEnumerable<string> ReadLines(string path) => throw new NotSupportedException();
        public IEnumerable<string> EnumerateFiles(string path, string pattern, SearchOption option) => throw new NotSupportedException();
        public IEnumerable<string> EnumerateDirectories(string path, string pattern, SearchOption option) => throw new NotSupportedException();
        public IEnumerable<string> EnumerateFileSystemEntries(string path) => throw new NotSupportedException();
    }

    private sealed class Catalog : IRuntimeAssetSource, IRuntimeAssetCatalog
    {
        public bool SupportsSynchronousReads => false;
        public bool IsAccelerated => false;
        public string Status => "test";
        public IAssetFileSystem? FileSystem => null;
        public string EngineAssetsRoot => "/engine";
        public string GameAssetsRoot => "/game";
        public bool TryGetAsset(string path, [NotNullWhen(true)] out RuntimeAssetCatalogEntry? entry)
        {
            entry = path == "/game/scenes/start.asset" ? new(path, "scene", RuntimeAssetEncoding.CookedBinary, []) : null;
            return entry is not null;
        }
        public bool Exists(string path) => throw new NotSupportedException();
        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken token = default) => throw new NotSupportedException();
        public byte[] ReadAllBytes(string path) => throw new NotSupportedException();
        public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken token = default) => throw new NotSupportedException();
        public byte[] ReadRange(string path, long offset, int length) => throw new NotSupportedException();
        public Task<byte[]> ReadRangeAsync(string path, long offset, int length, CancellationToken token = default) => throw new NotSupportedException();
        public unsafe bool TryReadInto(string path, long offset, int length, void* destination, CancellationToken token = default) => throw new NotSupportedException();
        public unsafe bool TryReadFileInto(string path, void* destination, int length, CancellationToken token = default) => throw new NotSupportedException();
        public IAssetReadBatch CreateBatch() => throw new NotSupportedException();
    }
}
