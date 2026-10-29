using System.IO.MemoryMappedFiles;
using NUnit.Framework;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Runtime.Platform.Desktop;

namespace XREngine.UnitTests.Core;

[TestFixture, NonParallelizable]
public sealed class XRAssetMappedStreamingTests
{
    private IFileMappingBackend? _previousBackend;
    private IRuntimeFileDiscovery? _previousFiles;
    private IRuntimeAssetReadSource? _previousSource;
    private bool _previousCallerThread;
    private IDisposable _cacheSuppression = null!;
    private IFileMappingBackend _native = null!;
    private string _root = null!;
    private string _path = null!;
    private readonly List<TestAsset> _assets = [];

    [SetUp]
    public void SetUp()
    {
        _previousBackend = FileMappingServices.Backend;
        _previousFiles = RuntimeFileDiscoveryServices.Current;
        _previousSource = RuntimeAssetReadServices.Source;
        _previousCallerThread = RuntimeAssetReadServices.IsCallerThread;
        RuntimeAssetReadServices.SetSource(null);
        RuntimeAssetReadServices.IsCallerThread = false;
        RuntimeFileDiscoveryServices.Current = new DesktopAssetFileSystem();
        _cacheSuppression = XRObjectBase.SuppressObjectCacheRegistration();
        Type backendType = typeof(DesktopAssetFileSystem).Assembly.GetType(
            "XREngine.Runtime.Platform.Desktop.DesktopFileMappingBackend", throwOnError: true)!;
        _native = (IFileMappingBackend)Activator.CreateInstance(backendType, nonPublic: true)!;
        FileMappingServices.Backend = _native;
        _root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "asset-mapping-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "asset.bin");
        byte[] bytes = new byte[4096];
        bytes[0] = 17;
        File.WriteAllBytes(_path, bytes);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (TestAsset asset in _assets)
        {
            asset.CloseStreaming();
            ((IDisposable)asset).Dispose();
        }
        _assets.Clear();
        FileMappingServices.Backend = _previousBackend;
        RuntimeAssetReadServices.IsCallerThread = _previousCallerThread;
        RuntimeAssetReadServices.SetSource(_previousSource);
        RuntimeFileDiscoveryServices.Current = _previousFiles;
        _cacheSuppression.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void DesktopViewKeepsWholeFileReadWriteAndFlushBehavior()
    {
        TestAsset asset = CreateAsset();
        asset.OpenForStreaming();
        MemoryMappedViewStream view = asset.FileMapStream!;
        Assert.That(view.Length, Is.EqualTo(4096));
        Assert.That(view.CanRead && view.CanWrite, Is.True);
        Assert.That(view.ReadByte(), Is.EqualTo(17));
        view.Position = 0;
        view.WriteByte(42);
        view.Flush();
        asset.CloseStreaming();
        Assert.That(asset.FileMapStream, Is.Null);
        Assert.Throws<ObjectDisposedException>(() => view.ReadByte());
        Assert.That(File.ReadAllBytes(_path)[0], Is.EqualTo(42));
    }

    [Test]
    public void ReopenClosesOldViewAndRepeatedCloseDisposesOnce()
    {
        var backend = new ProbeBackend((IFileMappedStreamBackend)_native);
        FileMappingServices.Backend = backend;
        TestAsset asset = CreateAsset();
        asset.OpenForStreaming();
        CountingLease first = backend.LastLease!;
        MemoryMappedViewStream firstView = asset.FileMapStream!;
        asset.OpenForStreaming();
        Assert.That(first.DisposeCalls, Is.EqualTo(1));
        Assert.Throws<ObjectDisposedException>(() => firstView.ReadByte());
        CountingLease second = backend.LastLease!;
        asset.CloseStreaming();
        asset.CloseStreaming();
        Assert.That(second.DisposeCalls, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void MissingMappingCapabilityKeepsExistingView(bool missingBackend)
    {
        TestAsset asset = CreateAsset();
        asset.OpenForStreaming();
        MemoryMappedViewStream view = asset.FileMapStream!;
        FileMappingServices.Backend = missingBackend ? null : new BasicBackend();
        Assert.Throws<NotSupportedException>(asset.OpenForStreaming);
        Assert.That(asset.FileMapStream, Is.SameAs(view));
        Assert.That(view.ReadByte(), Is.EqualTo(17));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void HostAdmissionRejectsBeforeBackendAndKeepsExistingView(bool callerThread)
    {
        TestAsset asset = CreateAsset();
        asset.OpenForStreaming();
        MemoryMappedViewStream view = asset.FileMapStream!;
        var backend = new ProbeBackend((IFileMappedStreamBackend)_native);
        FileMappingServices.Backend = backend;
        if (callerThread) RuntimeAssetReadServices.IsCallerThread = true;
        else RuntimeAssetReadServices.SetSource(new NonHostSource());
        Assert.Throws<NotSupportedException>(asset.OpenForStreaming);
        Assert.That(backend.Opens, Is.Zero);
        Assert.That(asset.FileMapStream, Is.SameAs(view));
    }

    [Test]
    public void MissingPathRejectsBeforeClosingExistingView()
    {
        TestAsset asset = CreateAsset();
        asset.OpenForStreaming();
        MemoryMappedViewStream view = asset.FileMapStream!;
        asset.FilePath = null;
        Assert.Throws<InvalidOperationException>(asset.OpenForStreaming);
        Assert.That(asset.FileMapStream, Is.SameAs(view));
    }

    [Test]
    public void BackendReplacementDoesNotRetargetOpenOrClose()
    {
        var replacement = new ProbeBackend((IFileMappedStreamBackend)_native);
        var original = new ProbeBackend((IFileMappedStreamBackend)_native)
        {
            OnOpen = () => FileMappingServices.Backend = replacement,
        };
        FileMappingServices.Backend = original;
        TestAsset asset = CreateAsset();
        asset.OpenForStreaming();
        Assert.That(original.Opens, Is.EqualTo(1));
        Assert.That(replacement.Opens, Is.Zero);
        asset.CloseStreaming();
        Assert.That(original.LastLease!.DisposeCalls, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ChangedSourceOrAdmissionDisposesUnpublishedView(bool callerThread)
    {
        var backend = new ProbeBackend((IFileMappedStreamBackend)_native)
        {
            OnOpen = () =>
            {
                if (callerThread) RuntimeAssetReadServices.IsCallerThread = true;
                else RuntimeAssetReadServices.SetSource(null);
            },
        };
        FileMappingServices.Backend = backend;
        TestAsset asset = CreateAsset();
        if (callerThread) Assert.Throws<NotSupportedException>(asset.OpenForStreaming);
        else Assert.Catch<OperationCanceledException>(asset.OpenForStreaming);
        Assert.That(asset.FileMapStream, Is.Null);
        Assert.That(backend.LastLease!.DisposeCalls, Is.EqualTo(1));
        Assert.Throws<ObjectDisposedException>(() => backend.LastLease.Stream.ReadByte());
        RuntimeAssetReadServices.IsCallerThread = false;
        FileMappingServices.Backend = _native;
        asset.OpenForStreaming();
        Assert.That(asset.FileMapStream!.ReadByte(), Is.EqualTo(17));
    }

    [Test]
    public void CompletedUnrelatedInstallationChangeDoesNotRejectMapping()
    {
        var backend = new ProbeBackend((IFileMappedStreamBackend)_native)
        {
            OnOpen = () => { using IDisposable change = RuntimeSourcePublicationServices.BeginInstallationChange(); },
        };
        FileMappingServices.Backend = backend;
        TestAsset asset = CreateAsset();
        asset.OpenForStreaming();
        Assert.That(asset.FileMapStream!.ReadByte(), Is.EqualTo(17));
    }

    [Test]
    public void ActiveInstallationChangeRejectsAndDisposesCandidate()
    {
        IDisposable? change = null;
        var backend = new ProbeBackend((IFileMappedStreamBackend)_native)
        {
            OnOpen = () => change = RuntimeSourcePublicationServices.BeginInstallationChange(),
        };
        FileMappingServices.Backend = backend;
        TestAsset asset = CreateAsset();
        try
        {
            Assert.Catch<OperationCanceledException>(asset.OpenForStreaming);
            Assert.That(backend.LastLease!.DisposeCalls, Is.EqualTo(1));
            Assert.That(asset.FileMapStream, Is.Null);
        }
        finally { change?.Dispose(); }
        FileMappingServices.Backend = _native;
        asset.OpenForStreaming();
    }

    [Test]
    public void FailedViewAccessDisposesCandidateAndAllowsRetry()
    {
        var backend = new ProbeBackend((IFileMappedStreamBackend)_native) { FailViewAccess = true };
        FileMappingServices.Backend = backend;
        TestAsset asset = CreateAsset();
        Assert.Throws<IOException>(asset.OpenForStreaming);
        Assert.That(backend.LastLease!.DisposeCalls, Is.EqualTo(1));
        Assert.That(asset.FileMapStream, Is.Null);
        FileMappingServices.Backend = _native;
        asset.OpenForStreaming();
        Assert.That(asset.FileMapStream!.ReadByte(), Is.EqualTo(17));
    }

    [Test]
    public void FailedNativeOpenClosesOldViewWithoutCreatingMissingFile()
    {
        TestAsset asset = CreateAsset();
        asset.OpenForStreaming();
        MemoryMappedViewStream oldView = asset.FileMapStream!;
        string missing = Path.Combine(_root, "missing.bin");
        asset.FilePath = missing;
        Assert.Throws<FileNotFoundException>(asset.OpenForStreaming);
        Assert.That(asset.FileMapStream, Is.Null);
        Assert.That(File.Exists(missing), Is.False);
        Assert.Throws<ObjectDisposedException>(() => oldView.ReadByte());
        asset.FilePath = _path;
        asset.OpenForStreaming();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ReentrantOpenOrCloseRejectsWithoutLosingOuterLease(bool reopen)
    {
        TestAsset asset = CreateAsset();
        var backend = new ProbeBackend((IFileMappedStreamBackend)_native)
        {
            OnOpen = () =>
            {
                InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                {
                    if (reopen) asset.OpenForStreaming();
                    else asset.CloseStreaming();
                })!;
                Assert.That(error.Message, Does.Contain("FileMapping.OperationInProgress"));
            },
        };
        FileMappingServices.Backend = backend;
        asset.OpenForStreaming();
        Assert.That(asset.FileMapStream!.ReadByte(), Is.EqualTo(17));
        asset.CloseStreaming();
        Assert.That(backend.LastLease!.DisposeCalls, Is.EqualTo(1));
    }

    private TestAsset CreateAsset()
    {
        var asset = new TestAsset { FilePath = _path };
        _assets.Add(asset);
        return asset;
    }

    private sealed class TestAsset : XRAsset { }

    private class BasicBackend : IFileMappingBackend
    {
        public bool FileExists(string path) => throw new NotSupportedException();
        public bool TryGetFileLength(string path, out long length) { length = 0; throw new NotSupportedException(); }
        public FileStream OpenFile(string path, bool writable, FileOptions options, Action<string, string> reportFallback) => throw new NotSupportedException();
        public FileStream OpenTemporaryFile(out string path) { path = null!; throw new NotSupportedException(); }
        public IFileMappingLease Map(FileStream stream, bool writable, long offset, long length) => throw new NotSupportedException();
    }

    private sealed class ProbeBackend(IFileMappedStreamBackend native) : BasicBackend, IFileMappedStreamBackend
    {
        public Action? OnOpen;
        public bool FailViewAccess;
        public int Opens;
        public CountingLease? LastLease;
        public IFileMappedStreamLease OpenMappedStream(string path)
        {
            Opens++;
            var lease = new CountingLease(native.OpenMappedStream(path), FailViewAccess);
            LastLease = lease;
            try { OnOpen?.Invoke(); return lease; }
            catch { lease.Dispose(); throw; }
        }
    }

    private sealed class CountingLease(IFileMappedStreamLease native, bool failViewAccess) : IFileMappedStreamLease
    {
        public int DisposeCalls;
        public MemoryMappedViewStream Stream => failViewAccess ? throw new IOException("view unavailable") : native.Stream;
        public void Dispose() { DisposeCalls++; native.Dispose(); }
    }

    private sealed class NonHostSource : IRuntimeAssetReadSource
    {
        public bool SupportsSynchronousReads => true;
        public bool SupportsHostFileAccess => false;
        public bool Exists(string path) => throw new NotSupportedException();
        public byte[] ReadAllBytes(string path) => throw new NotSupportedException();
        public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
