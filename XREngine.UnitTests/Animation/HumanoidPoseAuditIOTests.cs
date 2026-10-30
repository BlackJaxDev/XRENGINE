using System.Text;
using System.Reflection;
using NUnit.Framework;
using XREngine.Components.Animation;
using XREngine.Data;
using XREngine.Runtime.Platform.Desktop;

namespace XREngine.UnitTests.Animation;

[TestFixture, NonParallelizable]
public sealed class HumanoidPoseAuditIOTests
{
    private IRuntimeFileDiscovery? _previousFiles;
    private IRuntimeAssetReadSource? _previousSource;
    private IHostAssetFileOutput? _previousOutput;
    private bool _previousCallerThread;
    private string _root = null!;
    private ProbeFiles _files = null!;
    private ProbeOutput _output = null!;

    [SetUp]
    public void SetUp()
    {
        _previousFiles = RuntimeFileDiscoveryServices.Current;
        _previousSource = RuntimeAssetReadServices.Source;
        _previousOutput = HostAssetFileOutputServices.Current;
        _previousCallerThread = RuntimeAssetReadServices.IsCallerThread;
        RuntimeAssetReadServices.SetSource(null);
        RuntimeAssetReadServices.IsCallerThread = false;
        _root = Path.Combine(Path.GetTempPath(), "pose-audit-" + Guid.NewGuid().ToString("N"));
        _files = new ProbeFiles();
        _output = new ProbeOutput();
        RuntimeFileDiscoveryServices.Current = _files;
        HostAssetFileOutputServices.Current = _output;
    }

    [TearDown]
    public void TearDown()
    {
        RuntimeAssetReadServices.IsCallerThread = _previousCallerThread;
        RuntimeAssetReadServices.SetSource(_previousSource);
        RuntimeFileDiscoveryServices.Current = _previousFiles;
        HostAssetFileOutputServices.Current = _previousOutput;
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestCase("json")]
    [TestCase("json.gz")]
    public void ReportRoundTripsThroughDesktopOutputAndCapturedReader(string extension)
    {
        string path = Path.Combine(_root, "nested", "report." + extension);
        HumanoidPoseAuditIO.SaveReport(path, new HumanoidPoseAuditReport { ClipName = "test clip", SampleRate = 30, SampleCount = 3 });
        Assert.That(_output.Share, Is.EqualTo(FileShare.Read));
        Assert.That(HumanoidPoseAuditIO.TryLoadReport(path, out HumanoidPoseAuditReport? report), Is.True);
        Assert.That(report!.ClipName, Is.EqualTo("test clip"));
        Assert.That(report.SampleRate, Is.EqualTo(30));
        Assert.That(report.SampleCount, Is.EqualTo(3));
        Assert.That(_files.Share, Is.EqualTo(FileShare.Read));
        if (extension.EndsWith(".gz", StringComparison.Ordinal))
            Assert.That(File.ReadAllBytes(path).Take(2), Is.EqualTo(new byte[] { 0x1f, 0x8b }));
        else
            Assert.That(File.ReadAllBytes(path).Take(3), Is.Not.EqualTo(new byte[] { 0xef, 0xbb, 0xbf }));
    }

    [Test]
    public void MissingReportReturnsFalseWithoutOpening()
    {
        Assert.That(HumanoidPoseAuditIO.TryLoadReport(Path.Combine(_root, "missing.json"), out _), Is.False);
        Assert.That(_files.Reads, Is.Zero);
    }

    [Test]
    public void HostCapableSourceStillUsesCapturedDiscovery()
    {
        string path = SaveReport();
        RuntimeAssetReadServices.SetSource(new ProbeSource());
        HumanoidPoseAuditReport report = HumanoidPoseAuditIO.LoadReport(path);
        Assert.That(report.ClipName, Is.EqualTo("test"));
        Assert.That(_files.Reads, Is.EqualTo(1));
    }

    [Test]
    public void AuditComponentLoadsReferenceThroughHostProvider()
    {
        string path = SaveReport();
        var component = new HumanoidPoseAuditComponent { ReferencePath = path };
        MethodInfo method = typeof(HumanoidPoseAuditComponent).GetMethod("LoadReferenceReportIfAvailable", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var report = (HumanoidPoseAuditReport?)method.Invoke(component, null);
        Assert.That(report?.ClipName, Is.EqualTo("test"));
        Assert.That(_files.Reads, Is.EqualTo(1));
    }

    [Test]
    public void CallerThreadRejectsReadAndWriteBeforeProviderUse()
    {
        RuntimeAssetReadServices.IsCallerThread = true;
        string path = Path.Combine(_root, "report.json");
        Assert.Throws<NotSupportedException>(() => HumanoidPoseAuditIO.LoadReport(path));
        Assert.Throws<NotSupportedException>(() => HumanoidPoseAuditIO.SaveReport(path, new HumanoidPoseAuditReport()));
        Assert.That(_files.Reads, Is.Zero);
        Assert.That(_output.Writes, Is.Zero);
    }

    [Test]
    public void DiscoveryReplacementDuringReadRejectsReport()
    {
        string path = SaveReport();
        _files.OnOpen = () => RuntimeFileDiscoveryServices.Current = new ProbeFiles();
        Assert.Throws<OperationCanceledException>(() => HumanoidPoseAuditIO.LoadReport(path));
        using Stream exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Test]
    public void SourceReplacementDuringReadRejectsReport()
    {
        string path = SaveReport();
        _files.OnOpen = () => RuntimeAssetReadServices.SetSource(new ProbeSource());
        Assert.Throws<OperationCanceledException>(() => HumanoidPoseAuditIO.LoadReport(path));
        using Stream exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Test]
    public void MissingSharedOutputCapabilityDoesNotCreateFile()
    {
        HostAssetFileOutputServices.Current = new BasicOutput();
        string path = Path.Combine(_root, "report.json");
        Assert.Throws<NotSupportedException>(() => HumanoidPoseAuditIO.SaveReport(path, new HumanoidPoseAuditReport()));
        Assert.That(File.Exists(path), Is.False);
    }

    [Test]
    public void ComparisonUsesSameSharedOutput()
    {
        string path = Path.Combine(_root, "comparison.json");
        HumanoidPoseAuditIO.SaveComparison(path, new HumanoidPoseAuditComparisonReport());
        Assert.That(File.Exists(path), Is.True);
        Assert.That(_output.Share, Is.EqualTo(FileShare.Read));
    }

    [Test]
    public void FailedWriteDisposesOutputStream()
    {
        var stream = new FailingStream();
        _output.FailingStream = stream;
        Assert.Throws<IOException>(() => HumanoidPoseAuditIO.SaveReport(Path.Combine(_root, "report.json"), new HumanoidPoseAuditReport()));
        Assert.That(stream.Disposed, Is.True);
    }

    private string SaveReport()
    {
        string path = Path.Combine(_root, "report.json");
        HumanoidPoseAuditIO.SaveReport(path, new HumanoidPoseAuditReport { ClipName = "test" });
        return path;
    }

    private sealed class ProbeFiles : IRuntimeFileDiscovery, IRuntimeHostFileReadBackend
    {
        public int Reads;
        public FileShare Share;
        public Action? OnOpen;
        public bool FileExists(string path) => File.Exists(path);
        public Stream OpenRead(string path, FileShare share)
        {
            Reads++;
            Share = share;
            Stream stream = File.Open(path, FileMode.Open, FileAccess.Read, share);
            OnOpen?.Invoke();
            return stream;
        }
        public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);
        public IEnumerable<string> ReadLines(string path) => File.ReadLines(path);
        public IEnumerable<string> EnumerateFiles(string path, string pattern, SearchOption option) => Directory.EnumerateFiles(path, pattern, option);
        public IEnumerable<string> EnumerateDirectories(string path, string pattern, SearchOption option) => Directory.EnumerateDirectories(path, pattern, option);
        public IEnumerable<string> EnumerateFileSystemEntries(string path) => Directory.EnumerateFileSystemEntries(path);
    }

    private class BasicOutput : IHostAssetFileOutput
    {
        public Stream CreateFileWithParentDirectory(string path) => throw new NotSupportedException();
        public void WriteAllText(string path, string text, Encoding encoding) => throw new NotSupportedException();
        public Task WriteAllTextAsync(string path, string text, Encoding encoding) => throw new NotSupportedException();
    }

    private sealed class ProbeOutput : BasicOutput, IHostAssetFileSharedOutput
    {
        private readonly DesktopHostAssetFileOutput _desktop = new();
        public int Writes;
        public FileShare Share;
        public FailingStream? FailingStream;
        public Stream CreateFileWithParentDirectory(string path, FileShare share)
        {
            Writes++;
            Share = share;
            return FailingStream ?? _desktop.CreateFileWithParentDirectory(path, share);
        }
    }

    private sealed class FailingStream : MemoryStream
    {
        public bool Disposed;
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Expected write failure.");
        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("Expected write failure.");
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
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
