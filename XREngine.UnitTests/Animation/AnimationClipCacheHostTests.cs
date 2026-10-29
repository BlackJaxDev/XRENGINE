using System.Reflection;
using NUnit.Framework;
using XREngine;
using XREngine.Animation;
using XREngine.Core.Files;
using XREngine.Core.Files.Caching;
using XREngine.Data;
using XREngine.Runtime.Platform.Desktop;

namespace XREngine.UnitTests.Animation;

[TestFixture, NonParallelizable]
public sealed class AnimationClipCacheHostTests
{
    private IRuntimeFileDiscovery? _previousDiscovery;
    private IRuntimeAssetReadSource? _previousSource;
    private bool _previousCaller;
    private IDisposable? _registration;
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _previousDiscovery = RuntimeFileDiscoveryServices.Current;
        _previousSource = RuntimeAssetReadServices.Source;
        _previousCaller = RuntimeAssetReadServices.IsCallerThread;
        RuntimeAssetReadServices.SetSource(null);
        RuntimeAssetReadServices.IsCallerThread = false;
        _root = Path.Combine(Path.GetTempPath(), "animation-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        if (ThirdPartyCacheCodecRegistry.Find(typeof(AnimationClip)) is null)
            _registration = AnimationSerializationRegistration.Install();
    }

    [TearDown]
    public void TearDown()
    {
        _registration?.Dispose();
        RuntimeAssetReadServices.IsCallerThread = _previousCaller;
        RuntimeAssetReadServices.SetSource(_previousSource);
        RuntimeFileDiscoveryServices.Current = _previousDiscovery;
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Test]
    public void BackgroundWriteAndReadRetainCapturedProvider()
    {
        var owner = new ProbeFiles();
        var replacement = new ProbeFiles();
        RuntimeFileDiscoveryServices.Current = owner;
        var clip = new AnimationClip { LengthInSeconds = 1, OriginalLastWriteTimeUtc = DateTime.UtcNow };
        string path = Path.Combine(_root, "clip.asset");
        string original = Path.Combine(_root, "clip.anim");
        try
        {
            Assert.That(Queue(path, clip, owner), Is.True);
            Assert.That(owner.Entered.Wait(TimeSpan.FromSeconds(30)), Is.True, "Cache write did not start.");
            RuntimeFileDiscoveryServices.Current = replacement;
            owner.Continue.Set();
            Assert.That(owner.Done.Wait(TimeSpan.FromSeconds(30)), Is.True, "Cache write did not finish.");
            Assert.That(owner.Error, Is.Null);
            Assert.That(owner.Writes, Is.EqualTo(1));
            Assert.That(replacement.Writes, Is.Zero);
            CacheReadResult result = Read(path, original, clip.OriginalLastWriteTimeUtc!.Value, owner);
            Assert.That(result.Status, Is.EqualTo(CacheReadStatus.Hit));
            Assert.That(result.Asset!.OriginalPath, Is.EqualTo(original));
            Assert.That(owner.Reads, Is.EqualTo(1));
            Assert.That(replacement.Reads, Is.Zero);
        }
        finally
        {
            owner.Continue.Set();
            if (owner.Entered.IsSet) owner.Done.Wait(TimeSpan.FromSeconds(30));
        }
    }

    [Test]
    public void MissingBinaryCapabilityRejectsCacheRead()
    {
        var result = Read(Path.Combine(_root, "missing.asset"), "source.anim", DateTime.UtcNow, new MetadataOnly());
        Assert.That(result.Status, Is.EqualTo(CacheReadStatus.Rejected));
        Assert.That(result.Detail, Does.Contain("BinaryCacheUnavailable"));
    }

    [Test]
    public void MissingBinaryCapabilityRejectsCacheWrite()
    {
        var clip = new AnimationClip { LengthInSeconds = 1 };
        string path = Path.Combine(_root, "missing.asset");
        IThirdPartyCacheCodec codec = ThirdPartyCacheCodecRegistry.Find(typeof(AnimationClip))!;
        var result = (CacheWriteResult)typeof(AssetManager)
            .GetMethod("ExecuteRegisteredCachePayloadWrite", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [codec, path, clip, clip, new MetadataOnly()])!;
        Assert.That(result.Status, Is.EqualTo(CacheWriteStatus.Failed));
        Assert.That(result.Detail, Does.Contain("BinaryCacheUnavailable"));
        Assert.That(File.Exists(path), Is.False);
    }

    [Test]
    public void DesktopReplacementPreservesWholePayloadAndCleansTemporaryFiles()
    {
        var files = new DesktopAssetFileSystem();
        string path = Path.Combine(_root, "clip.asset");
        byte[] first = Enumerable.Repeat((byte)0x31, 4096).ToArray();
        byte[] second = Enumerable.Repeat((byte)0x72, 4096).ToArray();
        files.WriteAllBytesAtomic(path, first);
        Parallel.For(0, 12, i =>
        {
            try { files.WriteAllBytesAtomic(path, i % 2 == 0 ? first : second); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        });
        byte[] actual = File.ReadAllBytes(path);
        Assert.That(actual.SequenceEqual(first) || actual.SequenceEqual(second), Is.True);
        Assert.That(Directory.GetFiles(_root, "*.tmp"), Is.Empty);

        string destinationDirectory = Path.Combine(_root, "destination");
        Directory.CreateDirectory(destinationDirectory);
        Exception? error = Assert.Catch(() => files.WriteAllBytesAtomic(destinationDirectory, first));
        Assert.That(error, Is.InstanceOf<IOException>().Or.InstanceOf<UnauthorizedAccessException>());
        Assert.That(Directory.Exists(destinationDirectory), Is.True);
        Assert.That(Directory.GetFiles(_root, "*.tmp"), Is.Empty);
    }

    private static bool Queue(string path, AnimationClip clip, IAssetMetadataFileBackend files)
        => (bool)typeof(AssetManager).GetMethod("ShouldSuppressGenericCacheWrite", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [path, clip, clip, files])!;

    private static CacheReadResult Read(string path, string source, DateTime timestamp, IAssetMetadataFileBackend files)
        => (CacheReadResult)typeof(AssetManager).GetMethod("ReadRegisteredCachePayload", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [path, source, timestamp, typeof(AnimationClip), files, null])!;

    private class MetadataOnly : IAssetMetadataFileBackend
    {
        protected readonly DesktopAssetFileSystem Desktop = new();
        public bool FileExists(string path) => Desktop.FileExists(path);
        public bool DirectoryExists(string path) => Desktop.DirectoryExists(path);
        public void CreateDirectory(string path) => Desktop.CreateDirectory(path);
        public void DeleteDirectory(string path) => Desktop.DeleteDirectory(path);
        public void DeleteFile(string path) => Desktop.DeleteFile(path);
        public void MoveFile(string source, string destination, bool overwrite) => Desktop.MoveFile(source, destination, overwrite);
        public void CopyFile(string source, string destination, bool overwrite) => Desktop.CopyFile(source, destination, overwrite);
        public string ReadAllText(string path) => Desktop.ReadAllText(path);
        public void WriteAllText(string path, string text) => Desktop.WriteAllText(path, text);
        public Guid TryExtractAssetGuid(string path) => Desktop.TryExtractAssetGuid(path);
        public DateTime GetLastWriteTimeUtc(string path) => Desktop.GetLastWriteTimeUtc(path);
    }

    private sealed class ProbeFiles : MetadataOnly, IRuntimeFileDiscovery, IHostBinaryCacheFileBackend
    {
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim Continue = new();
        public readonly ManualResetEventSlim Done = new();
        public int Writes;
        public int Reads;
        public Exception? Error;
        public byte[] ReadAllBytes(string path) { Reads++; return Desktop.ReadAllBytes(path); }
        public Stream OpenRead(string path, FileShare share) => Desktop.OpenRead(path, share);
        public IEnumerable<string> ReadLines(string path) => Desktop.ReadLines(path);
        public IEnumerable<string> EnumerateFiles(string path, string pattern, SearchOption option) => Desktop.EnumerateFiles(path, pattern, option);
        public IEnumerable<string> EnumerateDirectories(string path, string pattern, SearchOption option) => Desktop.EnumerateDirectories(path, pattern, option);
        public IEnumerable<string> EnumerateFileSystemEntries(string path) => Desktop.EnumerateFileSystemEntries(path);
        public void WriteAllBytesAtomic(string path, byte[] bytes)
        {
            Writes++;
            Entered.Set();
            try
            {
                if (!Continue.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException();
                Desktop.WriteAllBytesAtomic(path, bytes);
            }
            catch (Exception ex) { Error = ex; throw; }
            finally { Done.Set(); }
        }
    }
}
