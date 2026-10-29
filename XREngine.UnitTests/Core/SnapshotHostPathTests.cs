using System.Reflection;
using NUnit.Framework;
using XREngine.Core.Files;
using XREngine.Data;

namespace XREngine.UnitTests.Core;

[TestFixture, NonParallelizable]
public sealed class SnapshotHostPathTests
{
    private IRuntimeFileDiscovery? _previousFiles;
    private IRuntimeAssetReadSource? _previousSource;
    private bool _previousCallerThread;
    private ProbeFiles _files = null!;
    private string _engineRoot = null!;
    private string _gameRoot = null!;

    [SetUp]
    public void SetUp()
    {
        _previousFiles = RuntimeFileDiscoveryServices.Current;
        _previousSource = RuntimeAssetReadServices.Source;
        _previousCallerThread = RuntimeAssetReadServices.IsCallerThread;
        RuntimeAssetReadServices.SetSource(null);
        RuntimeAssetReadServices.IsCallerThread = false;
        _files = new ProbeFiles();
        RuntimeFileDiscoveryServices.Current = _files;
        _engineRoot = Path.Combine(Path.GetTempPath(), "snapshot-engine-" + Guid.NewGuid().ToString("N"));
        _gameRoot = Path.Combine(Path.GetTempPath(), "snapshot-game-" + Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void TearDown()
    {
        RuntimeAssetReadServices.IsCallerThread = _previousCallerThread;
        RuntimeAssetReadServices.SetSource(_previousSource);
        RuntimeFileDiscoveryServices.Current = _previousFiles;
    }

    [Test]
    public void RelativeCandidatesKeepOrderAndLazyProbes()
    {
        string engine = Path.Combine(_engineRoot, "file");
        string shader = Path.Combine(_engineRoot, "Shaders", "file");
        string game = Path.Combine(_gameRoot, "file");
        _files.Found.Add(shader);
        _files.Found.Add(game);
        using RuntimeAssetReadLease read = RuntimeAssetReadServices.Capture();
        IRuntimeFileDiscovery? owner = RuntimeFileDiscoveryServices.Capture(out long generation);
        using var iterator = SnapshotAssetReference.EnumerateRelativeCandidatePaths("file", _engineRoot, _gameRoot, read, owner, generation).GetEnumerator();
        Assert.That(iterator.MoveNext(), Is.True);
        Assert.That(iterator.Current, Is.EqualTo(shader));
        Assert.That(_files.Seen, Is.EqualTo(new[] { engine, shader }));
        Assert.That(iterator.MoveNext(), Is.True);
        Assert.That(iterator.Current, Is.EqualTo(game));
        Assert.That(iterator.MoveNext(), Is.False);
    }

    [Test]
    public void ProviderReplacementBetweenCandidatesRejectsRemainingScan()
    {
        _files.Found.Add(Path.Combine(_engineRoot, "file"));
        using RuntimeAssetReadLease read = RuntimeAssetReadServices.Capture();
        IRuntimeFileDiscovery? owner = RuntimeFileDiscoveryServices.Capture(out long generation);
        using var iterator = SnapshotAssetReference.EnumerateRelativeCandidatePaths("file", _engineRoot, _gameRoot, read, owner, generation).GetEnumerator();
        Assert.That(iterator.MoveNext(), Is.True);
        RuntimeFileDiscoveryServices.Current = new ProbeFiles();
        Assert.Throws<OperationCanceledException>(() => iterator.MoveNext());
    }

    [Test]
    public void SourceReplacementDuringProbeRejectsResult()
    {
        _files.OnProbe = () => RuntimeAssetReadServices.SetSource(new ProbeSource());
        string path = Path.Combine(_engineRoot, "file.asset");
        Assert.Throws<OperationCanceledException>(() => SnapshotAssetReference.HasLoadableFile(path));
    }

    [Test]
    public void RootedPathUsesProviderAndDefaultFallback()
    {
        string path = Path.Combine(_engineRoot, "file.asset");
        _files.Found.Add(path);
        Assert.That(SnapshotAssetReference.HasLoadableFile(path), Is.True);
        Assert.That(_files.Seen, Is.EqualTo(new[] { path }));
        RuntimeFileDiscoveryServices.Current = null;
        Assert.That(SnapshotAssetReference.HasLoadableFile(path), Is.False);
    }

    [Test]
    public void SnapshotDecisionUsesReferenceForAvailableFile()
    {
        string path = Path.Combine(_engineRoot, "file.asset");
        _files.Found.Add(path);
        Assert.That(ShouldInline(new StubAsset { FilePath = path }), Is.False);
        Assert.That(_files.Seen, Is.EqualTo(new[] { path }));
    }

    [Test]
    public void CallerThreadSnapshotDecisionInlinesWithoutProbe()
    {
        RuntimeAssetReadServices.IsCallerThread = true;
        Assert.That(ShouldInline(new StubAsset { FilePath = Path.Combine(_engineRoot, "file.asset") }), Is.True);
        Assert.That(_files.Seen, Is.Empty);
    }

    [Test]
    public void CallerThreadSnapshotRoundTripsInlineAssetWithoutProbe()
    {
        RuntimeAssetReadServices.IsCallerThread = true;
        var asset = new StubAsset { FilePath = Path.Combine(_engineRoot, "file.asset"), Marker = "inline-marker" };
        byte[]? payload = SnapshotBinarySerializer.Serialize(asset);
        StubAsset? restored = SnapshotBinarySerializer.Deserialize<StubAsset>(payload);
        Assert.That(restored?.Marker, Is.EqualTo("inline-marker"));
        Assert.That(_files.Seen, Is.Empty);
    }

    private static bool ShouldInline(XRAsset asset)
    {
        MethodInfo method = typeof(SnapshotBinarySerializer).GetMethod("ShouldInlineAsset", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (bool)method.Invoke(null, [asset, null])!;
    }

    private sealed class StubAsset : XRAsset
    {
        public string Marker { get; set; } = string.Empty;
    }

    private sealed class ProbeFiles : IRuntimeFileDiscovery, IRuntimeHostFileReadBackend
    {
        public HashSet<string> Found = [];
        public List<string> Seen = [];
        public Action? OnProbe;
        public bool FileExists(string path) { Seen.Add(path); OnProbe?.Invoke(); return Found.Contains(path); }
        public Stream OpenRead(string path, FileShare share) => throw new NotSupportedException();
        public byte[] ReadAllBytes(string path) => throw new NotSupportedException();
        public IEnumerable<string> ReadLines(string path) => throw new NotSupportedException();
        public IEnumerable<string> EnumerateFiles(string path, string pattern, SearchOption option) => throw new NotSupportedException();
        public IEnumerable<string> EnumerateDirectories(string path, string pattern, SearchOption option) => throw new NotSupportedException();
        public IEnumerable<string> EnumerateFileSystemEntries(string path) => throw new NotSupportedException();
    }

    private sealed class ProbeSource : IRuntimeAssetReadSource
    {
        public bool SupportsSynchronousReads => true;
        public bool SupportsHostFileAccess => true;
        public bool Exists(string path) => false;
        public byte[] ReadAllBytes(string path) => throw new NotSupportedException();
        public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken token = default) => throw new NotSupportedException();
    }
}
