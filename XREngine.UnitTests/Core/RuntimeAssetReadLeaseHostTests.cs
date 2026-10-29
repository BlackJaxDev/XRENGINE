using NUnit.Framework;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Runtime.Platform.Desktop;

namespace XREngine.UnitTests.Core;

[TestFixture, NonParallelizable]
public sealed class RuntimeAssetReadLeaseHostTests
{
    private IRuntimeFileDiscovery? _previousDiscovery;
    private IAssetFileSystem? _previousFiles;
    private IRuntimeAssetReadSource? _previousSource;
    private bool _previousCaller;
    private AsyncFiles _files = null!;

    [SetUp]
    public void SetUp()
    {
        _previousDiscovery = RuntimeFileDiscoveryServices.Current;
        _previousFiles = AssetFileSystemServices.Current;
        _previousSource = RuntimeAssetReadServices.Source;
        _previousCaller = RuntimeAssetReadServices.IsCallerThread;
        RuntimeAssetReadServices.SetSource(null);
        RuntimeAssetReadServices.IsCallerThread = false;
        _files = new AsyncFiles();
        AssetFileSystemServices.Current = _files;
    }

    [TearDown]
    public void TearDown()
    {
        RuntimeAssetReadServices.SetSource(_previousSource);
        RuntimeAssetReadServices.IsCallerThread = _previousCaller;
        AssetFileSystemServices.Current = _previousFiles;
        RuntimeFileDiscoveryServices.Current = _previousDiscovery;
    }

    [Test]
    public async Task FallbackUsesCapturedProviderForProbeAndBothReadModes()
    {
        using RuntimeAssetReadLease read = RuntimeAssetReadServices.Capture();
        Assert.That(read.Exists("virtual/source"), Is.True);
        Assert.That(read.ReadAllBytes("virtual/source"), Is.EqualTo(_files.Bytes));
        Assert.That(await read.ReadAllBytesAsync("virtual/source"), Is.EqualTo(_files.Bytes));
        Assert.That(_files.Probes, Is.EqualTo(1));
        Assert.That(_files.Reads, Is.EqualTo(1));
        Assert.That(_files.AsyncReads, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void MissingOrEnumerationOnlyProviderHasNamedFailure(bool enumerationOnly)
    {
        RuntimeFileDiscoveryServices.Current = enumerationOnly ? new EnumerationOnly() : null;
        using RuntimeAssetReadLease read = RuntimeAssetReadServices.Capture();
        Assert.That(Assert.Throws<NotSupportedException>(() => read.Exists("source"))!.Message,
            Does.Contain("AssetSource.HostFileReadUnavailable"));
        Assert.That(Assert.Throws<NotSupportedException>(() => read.ReadAllBytes("source"))!.Message,
            Does.Contain("AssetSource.HostFileReadUnavailable"));
        Assert.That(Assert.ThrowsAsync<NotSupportedException>(async () => await read.ReadAllBytesAsync("source"))!.Message,
            Does.Contain("AssetSource.HostFileAsyncReadUnavailable"));
    }

    [Test]
    public void AsyncReadRequiresAsyncCapabilityWithoutSynchronousFallback()
    {
        var files = new Files();
        RuntimeFileDiscoveryServices.Current = files;
        using RuntimeAssetReadLease read = RuntimeAssetReadServices.Capture();
        Assert.That(Assert.ThrowsAsync<NotSupportedException>(async () => await read.ReadAllBytesAsync("source"))!.Message,
            Does.Contain("AssetSource.HostFileAsyncReadUnavailable"));
        Assert.That(files.Reads + files.Probes, Is.Zero);
    }

    [Test]
    public void CallerThreadRejectsFallbackBeforeCallingProvider()
    {
        RuntimeAssetReadServices.IsCallerThread = true;
        using RuntimeAssetReadLease read = RuntimeAssetReadServices.Capture();
        Assert.That(read.TryGetExists("source", out _), Is.False);
        Assert.Throws<NotSupportedException>(() => read.Exists("source"));
        Assert.Throws<NotSupportedException>(() => read.ReadAllBytes("source"));
        Assert.ThrowsAsync<NotSupportedException>(async () => await read.ReadAllBytesAsync("source"));
        Assert.That(_files.Probes + _files.Reads + _files.AsyncReads, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DiscoveryReplacementBetweenProbeAndReadDoesNotRetargetLease(bool sameInstance)
    {
        using RuntimeAssetReadLease read = RuntimeAssetReadServices.Capture();
        Assert.That(read.Exists("source"), Is.True);
        var replacement = new AsyncFiles();
        RuntimeFileDiscoveryServices.Current = sameInstance ? _files : replacement;
        Assert.Throws<OperationCanceledException>(() => read.ReadAllBytes("source"));
        Assert.ThrowsAsync<OperationCanceledException>(async () => await read.ReadAllBytesAsync("source"));
        Assert.That(_files.Reads + _files.AsyncReads + replacement.Reads + replacement.AsyncReads, Is.Zero);
    }

    [Test]
    public void ProviderInstalledAfterCaptureDoesNotRepairAbsentOwner()
    {
        RuntimeFileDiscoveryServices.Current = null;
        using RuntimeAssetReadLease read = RuntimeAssetReadServices.Capture();
        RuntimeFileDiscoveryServices.Current = _files;
        Assert.Throws<OperationCanceledException>(() => read.Exists("source"));
        Assert.That(_files.Probes, Is.Zero);
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void OwnerReplacementDuringProviderCallRejectsResultOrFailure(bool replaceSource, bool fail)
    {
        _files.OnAccess = () =>
        {
            if (replaceSource) RuntimeAssetReadServices.SetSource(new RawSource());
            else RuntimeFileDiscoveryServices.Current = new AsyncFiles();
            if (fail) throw new IOException("provider failed after replacement");
        };
        using RuntimeAssetReadLease read = RuntimeAssetReadServices.Capture();
        Assert.Catch<OperationCanceledException>(() => read.ReadAllBytes("source"));
    }

    [Test]
    public void DiscoveryReplacementDuringProbeRejectsMissingResult()
    {
        _files.Exists = false;
        _files.OnAccess = () => RuntimeFileDiscoveryServices.Current = _files;
        using RuntimeAssetReadLease read = RuntimeAssetReadServices.Capture();
        Assert.Throws<OperationCanceledException>(() => read.Exists("source"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DiscoveryReplacementDuringAsyncReadRejectsCompletionOrFailure(bool fail)
    {
        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _files.Pending = completion.Task;
        using RuntimeAssetReadLease read = RuntimeAssetReadServices.Capture();
        Task<byte[]> pending = read.ReadAllBytesAsync("source");
        RuntimeFileDiscoveryServices.Current = new AsyncFiles();
        if (fail) completion.SetException(new IOException("provider failed after replacement"));
        else completion.SetResult([9]);
        Assert.ThrowsAsync<OperationCanceledException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CallerCancellationAndSourceReplacementCancelPendingProviderRead(bool replaceSource)
    {
        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _files.Pending = completion.Task;
        using var cancellation = new CancellationTokenSource();
        using RuntimeAssetReadLease read = RuntimeAssetReadServices.Capture(cancellation.Token);
        Task<byte[]> pending = read.ReadAllBytesAsync("source");
        if (replaceSource) RuntimeAssetReadServices.SetSource(null);
        else cancellation.Cancel();
        Assert.That(_files.LastToken.IsCancellationRequested, Is.True);
        Assert.CatchAsync<OperationCanceledException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Test]
    public void PreCanceledReadDoesNotCallProvider()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using RuntimeAssetReadLease read = RuntimeAssetReadServices.Capture(cancellation.Token);
        Assert.Catch<OperationCanceledException>(() => read.Exists("source"));
        Assert.CatchAsync<OperationCanceledException>(async () => await read.ReadAllBytesAsync("source"));
        Assert.That(_files.Probes + _files.AsyncReads, Is.Zero);
    }

    [Test]
    public async Task InstalledRawSourceRemainsIndependentOfDiscoveryChanges()
    {
        var source = new RawSource();
        RuntimeAssetReadServices.SetSource(source);
        using RuntimeAssetReadLease read = RuntimeAssetReadServices.Capture();
        RuntimeFileDiscoveryServices.Current = null;
        Assert.That(read.Exists("source"), Is.True);
        Assert.That(read.ReadAllBytes("source"), Is.EqualTo(source.Bytes));
        Assert.That(await read.ReadAllBytesAsync("source"), Is.EqualTo(source.Bytes));
        using (read.BeginPublication())
            RuntimeFileDiscoveryServices.Current = new AsyncFiles();
        Assert.That(source.Calls, Is.EqualTo(3));
        Assert.That(_files.Probes + _files.Reads + _files.AsyncReads, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PendingInstalledSourceReadSurvivesDiscoveryReplacement(bool hostAccess)
    {
        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new RawSource { SupportsHostFileAccess = hostAccess, Pending = completion.Task };
        RuntimeAssetReadServices.SetSource(source);
        RuntimeAssetReadServices.IsCallerThread = !hostAccess;
        using RuntimeAssetReadLease read = RuntimeAssetReadServices.Capture();
        Task<byte[]> pending = read.ReadAllBytesAsync("source");
        RuntimeFileDiscoveryServices.Current = new AsyncFiles();
        completion.SetResult(source.Bytes);
        Assert.That(await pending.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(source.Bytes));
        using IDisposable publication = read.BeginPublication();
        Assert.That(source.Calls, Is.EqualTo(1));
        Assert.That(_files.AsyncReads, Is.Zero);
    }

    [Test]
    public async Task AsyncOnlyNonHostSourceKeepsCallerThreadAdmission()
    {
        var source = new RawSource { SupportsHostFileAccess = false, SupportsSynchronousReads = false };
        RuntimeAssetReadServices.SetSource(source);
        RuntimeAssetReadServices.IsCallerThread = true;
        using RuntimeAssetReadLease read = RuntimeAssetReadServices.Capture();
        Assert.That(read.TryGetExists("source", out bool exists), Is.True);
        Assert.That(exists, Is.True);
        Assert.That(Assert.Throws<NotSupportedException>(() => read.ReadAllBytes("source"))!.Message,
            Does.Contain("AssetSource.AsyncReadRequired"));
        Assert.That(await read.ReadAllBytesAsync("source"), Is.EqualTo(source.Bytes));
        Assert.That(_files.Probes + _files.Reads + _files.AsyncReads, Is.Zero);
    }

    [Test]
    public void PublicationBlocksBothDiscoverySettersBeforeEitherStateChanges()
    {
        using RuntimeAssetReadLease read = RuntimeAssetReadServices.Capture();
        var replacement = new AsyncFiles();
        using (read.BeginPublication())
        {
            Assert.Throws<InvalidOperationException>(() => RuntimeFileDiscoveryServices.Current = replacement);
            Assert.Throws<InvalidOperationException>(() => AssetFileSystemServices.Current = replacement);
            Assert.Throws<InvalidOperationException>(() => RuntimeAssetReadServices.SetSource(null));
            Assert.That(RuntimeFileDiscoveryServices.Current, Is.SameAs(_files));
            Assert.That(AssetFileSystemServices.Current, Is.SameAs(_files));
        }
        AssetFileSystemServices.Current = replacement;
        Assert.That(RuntimeFileDiscoveryServices.Current, Is.SameAs(replacement));
    }

    [Test]
    public void StalePublicationReleasesItsGlobalReservation()
    {
        using RuntimeAssetReadLease read = RuntimeAssetReadServices.Capture();
        RuntimeFileDiscoveryServices.Current = new AsyncFiles();
        Assert.Throws<OperationCanceledException>(() => read.BeginPublication());
        Assert.DoesNotThrow(() => AssetFileSystemServices.Current = _files);
        Assert.DoesNotThrow(() => RuntimeAssetReadServices.SetSource(null));
    }

    [Test]
    public void CompletedUnrelatedInstallationChangeDoesNotRetireFallbackRead()
    {
        using RuntimeAssetReadLease read = RuntimeAssetReadServices.Capture();
        using (RuntimeSourcePublicationServices.BeginInstallationChange()) { }
        Assert.That(read.ReadAllBytes("source"), Is.EqualTo(_files.Bytes));
        using IDisposable publication = read.BeginPublication();
    }

    [Test]
    public void PublicationRejectsActiveInstallationWithoutLeakingReservation()
    {
        using RuntimeAssetReadLease read = RuntimeAssetReadServices.Capture();
        using (RuntimeSourcePublicationServices.BeginInstallationChange())
            Assert.Throws<OperationCanceledException>(() => read.BeginPublication());
        using IDisposable publication = read.BeginPublication();
    }

    [Test]
    public void HostProbeHelperUsesRawSourceWithoutRecursionWhenDiscoveryIsAbsent()
    {
        RuntimeFileDiscoveryServices.Current = null;
        var source = new RawSource();
        RuntimeAssetReadServices.SetSource(source);
        using RuntimeAssetReadLease read = RuntimeAssetReadServices.Capture();
        Assert.That(RuntimeFileDiscoveryServices.HostFileExists(read, "source", "test probe"), Is.True);
        Assert.That(source.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task ExplicitDesktopCompositionLoadsTextSynchronouslyAndAsynchronously()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "host text");
            AssetFileSystemServices.Current = new DesktopAssetFileSystem();
            using IDisposable suppression = XRObjectBase.SuppressObjectCacheRegistration();
            using var text = new TextFile();
            Assert.That(text.LoadText(path), Is.True);
            Assert.That(text.Text, Is.EqualTo("host text"));
            Assert.That(await text.LoadTextAsync(path), Is.True);
            Assert.That(text.Text, Is.EqualTo("host text"));
        }
        finally { File.Delete(path); }
    }

    private class EnumerationOnly : IRuntimeFileDiscovery
    {
        public IEnumerable<string> EnumerateFiles(string path, string pattern, SearchOption option) => throw new NotSupportedException();
        public IEnumerable<string> EnumerateDirectories(string path, string pattern, SearchOption option) => throw new NotSupportedException();
        public IEnumerable<string> EnumerateFileSystemEntries(string path) => throw new NotSupportedException();
    }

    private class Files : EnumerationOnly, IAssetFileSystem, IRuntimeHostFileReadBackend
    {
        public bool Exists = true;
        public byte[] Bytes = [1, 2, 3];
        public int Probes;
        public int Reads;
        public Action? OnAccess;
        public bool SupportsChangeNotifications => false;
        public IAssetChangeMonitor CreateChangeMonitor() => throw new NotSupportedException();
        public bool FileExists(string path) { Probes++; OnAccess?.Invoke(); return Exists; }
        public byte[] ReadAllBytes(string path) { Reads++; OnAccess?.Invoke(); return Bytes; }
        public Stream OpenRead(string path, FileShare share) => throw new NotSupportedException();
        public IEnumerable<string> ReadLines(string path) => throw new NotSupportedException();
    }

    private sealed class AsyncFiles : Files, IRuntimeHostFileAsyncReadBackend
    {
        public Task<byte[]>? Pending;
        public CancellationToken LastToken;
        public int AsyncReads;
        public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken)
        {
            AsyncReads++;
            LastToken = cancellationToken;
            OnAccess?.Invoke();
            return Pending?.WaitAsync(cancellationToken) ?? Task.FromResult(Bytes);
        }
    }

    private sealed class RawSource : IRuntimeAssetReadSource
    {
        public byte[] Bytes = [4, 5];
        public Task<byte[]>? Pending;
        public int Calls;
        public bool SupportsHostFileAccess { get; init; } = true;
        public bool SupportsSynchronousReads { get; init; } = true;
        public bool Exists(string path) { Calls++; return true; }
        public byte[] ReadAllBytes(string path) { Calls++; return Bytes; }
        public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default)
        { Calls++; return Pending?.WaitAsync(cancellationToken) ?? Task.FromResult(Bytes); }
    }
}
