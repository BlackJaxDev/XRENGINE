using System.Text;
using NUnit.Framework;
using XREngine.Components.Animation;
using XREngine.Core.Files;
using XREngine.Data;

namespace XREngine.UnitTests.Animation;

[TestFixture, NonParallelizable]
public sealed class HumanoidConformanceSourceScanHostTests
{
    private IRuntimeFileDiscovery? _previousDiscovery;
    private IRuntimeAssetReadSource? _previousSource;
    private bool _previousCaller;
    private ProbeFiles _files = null!;
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _previousDiscovery = RuntimeFileDiscoveryServices.Current;
        _previousSource = RuntimeAssetReadServices.Source;
        _previousCaller = RuntimeAssetReadServices.IsCallerThread;
        RuntimeAssetReadServices.SetSource(null);
        RuntimeAssetReadServices.IsCallerThread = false;
        _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "virtual-scan-" + Guid.NewGuid().ToString("N")));
        _files = new ProbeFiles(_root);
        RuntimeFileDiscoveryServices.Current = _files;
    }

    [TearDown]
    public void TearDown()
    {
        RuntimeAssetReadServices.IsCallerThread = _previousCaller;
        RuntimeAssetReadServices.SetSource(_previousSource);
        RuntimeFileDiscoveryServices.Current = _previousDiscovery;
    }

    [Test]
    public void UsesOneVirtualProviderAndDetectsUtf16WithBom()
    {
        var result = Scan();
        Assert.That(result.Errors, Is.Empty);
        Assert.That(result.ScannedFileCount, Is.EqualTo(1));
        Assert.That(result.Findings, Has.Count.EqualTo(1));
        Assert.That(result.Findings[0].Line, Is.EqualTo(2));
        Assert.That(result.Findings[0].Identity, Is.EqualTo("fixture"));
        Assert.That(_files.Opens, Is.EqualTo(1));
        Assert.That(_files.Share, Is.EqualTo(FileShare.Read));
        Assert.That(_files.LastStream!.Disposed, Is.True);
    }

    [Test]
    public void HostCapableSourceStillUsesCapturedDiscovery()
    {
        RuntimeAssetReadServices.SetSource(new ProbeSource());
        HumanoidConformanceDependencyScanResult result = Scan();
        Assert.That(result.Errors, Is.Empty);
        Assert.That(result.Findings, Has.Count.EqualTo(1));
        Assert.That(_files.Opens, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ReplacedOwnerRejectsResultAndDisposesRead(bool replaceSource)
    {
        _files.OnOpen = replaceSource
            ? () => RuntimeAssetReadServices.SetSource(new ProbeSource())
            : () => RuntimeFileDiscoveryServices.Current = new ProbeFiles(_root);
        Assert.Throws<OperationCanceledException>(() => Scan());
        Assert.That(_files.LastStream!.Disposed, Is.True);
    }

    [Test]
    public void CallerThreadIsRejectedBeforeProviderUse()
    {
        RuntimeAssetReadServices.IsCallerThread = true;
        Assert.Throws<NotSupportedException>(() => Scan());
        Assert.That(_files.DirectoryChecks, Is.Zero);
        Assert.That(_files.Opens, Is.Zero);
    }

    [Test]
    public void MissingProductionRootIsReported()
    {
        var result = HumanoidConformanceSourceDependencyScanner.Scan([Path.Combine(_root, "missing")], [], ["fixture"]);
        Assert.That(result.Errors.Any(error => error.Contains("does not exist", StringComparison.Ordinal)), Is.True);
        Assert.That(_files.Opens, Is.Zero);
    }

    [Test]
    public void MissingDirectoryCapabilityFailsBeforeEnumeration()
    {
        RuntimeFileDiscoveryServices.Current = new EnumerationOnly();
        Assert.Throws<NotSupportedException>(() => Scan());
    }

    [Test]
    public void ReadFailureIsReported()
    {
        _files.FailOpen = true;
        var result = Scan();
        Assert.That(result.Errors.Any(error => error.Contains("expected read failure", StringComparison.Ordinal)), Is.True);
        Assert.That(result.ScannedFileCount, Is.EqualTo(1));
    }

    private HumanoidConformanceDependencyScanResult Scan()
        => HumanoidConformanceSourceDependencyScanner.Scan([_root], [Path.Combine(_root, "allowed")], ["fixture"]);

    private sealed class ProbeFiles(string root) : IRuntimeFileDiscovery, IRuntimeHostFileReadBackend, IAssetMetadataFileBackend
    {
        public int DirectoryChecks;
        public int Opens;
        public bool FailOpen;
        public FileShare Share;
        public Action? OnOpen;
        public TrackedStream? LastStream;
        public bool DirectoryExists(string path) { DirectoryChecks++; return path == root || path == Path.Combine(root, "allowed"); }
        public IEnumerable<string> EnumerateFiles(string path, string pattern, SearchOption option)
            => [Path.Combine(root, "source.cs"), Path.Combine(root, "allowed", "ignored.cs"), Path.Combine(root, "ignored.bin")];
        public IEnumerable<string> EnumerateDirectories(string path, string pattern, SearchOption option) => [];
        public IEnumerable<string> EnumerateFileSystemEntries(string path) => [];
        public Stream OpenRead(string path, FileShare share)
        {
            Opens++; Share = share;
            if (FailOpen) throw new IOException("expected read failure");
            LastStream = new TrackedStream(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("first\nFIXTURE\n")).ToArray());
            OnOpen?.Invoke();
            return LastStream;
        }
        public bool FileExists(string path) => throw new NotSupportedException();
        public byte[] ReadAllBytes(string path) => throw new NotSupportedException();
        public IEnumerable<string> ReadLines(string path) => throw new NotSupportedException();
        public void CreateDirectory(string path) => throw new NotSupportedException();
        public void DeleteDirectory(string path) => throw new NotSupportedException();
        public void DeleteFile(string path) => throw new NotSupportedException();
        public void MoveFile(string source, string destination, bool overwrite) => throw new NotSupportedException();
        public void CopyFile(string source, string destination, bool overwrite) => throw new NotSupportedException();
        public string ReadAllText(string path) => throw new NotSupportedException();
        public void WriteAllText(string path, string contents) => throw new NotSupportedException();
        public DateTime GetLastWriteTimeUtc(string path) => throw new NotSupportedException();
        public Guid TryExtractAssetGuid(string path) => throw new NotSupportedException();
    }

    private sealed class TrackedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool Disposed;
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class EnumerationOnly : IRuntimeFileDiscovery
    {
        public IEnumerable<string> EnumerateFiles(string path, string pattern, SearchOption option) => throw new InvalidOperationException();
        public IEnumerable<string> EnumerateDirectories(string path, string pattern, SearchOption option) => throw new InvalidOperationException();
        public IEnumerable<string> EnumerateFileSystemEntries(string path) => throw new InvalidOperationException();
    }

    private sealed class ProbeSource : IRuntimeAssetReadSource
    {
        public bool SupportsSynchronousReads => true;
        public bool SupportsHostFileAccess => true;
        public bool Exists(string path) => throw new NotSupportedException();
        public byte[] ReadAllBytes(string path) => throw new NotSupportedException();
        public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken token = default) => throw new NotSupportedException();
    }
}
