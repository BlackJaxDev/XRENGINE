using System.Text;
using NUnit.Framework;
using XREngine.Data;
using XREngine.Data.MMD;

namespace XREngine.UnitTests.Animation;

[TestFixture, NonParallelizable]
public sealed class VmdHostFileTests
{
    private IRuntimeFileDiscovery? _previousFiles;
    private IRuntimeAssetReadSource? _previousSource;
    private bool _previousCallerThread;
    private ProbeFiles _files = null!;

    [SetUp]
    public void SetUp()
    {
        _previousFiles = RuntimeFileDiscoveryServices.Current;
        _previousSource = RuntimeAssetReadServices.Source;
        _previousCallerThread = RuntimeAssetReadServices.IsCallerThread;
        RuntimeAssetReadServices.SetSource(null);
        RuntimeAssetReadServices.IsCallerThread = false;
        _files = new ProbeFiles(ValidData());
        RuntimeFileDiscoveryServices.Current = _files;
    }

    [TearDown]
    public void TearDown()
    {
        RuntimeAssetReadServices.IsCallerThread = _previousCallerThread;
        RuntimeAssetReadServices.SetSource(_previousSource);
        RuntimeFileDiscoveryServices.Current = _previousFiles;
    }

    [Test]
    public void LoadUsesCapturedProviderAndDisposesStream()
    {
        var replacement = new ProbeFiles(ValidData());
        _files.OnOpen = () => RuntimeFileDiscoveryServices.Current = replacement;
        var vmd = new VMDFile();
        vmd.Load("virtual.vmd");
        Assert.That(vmd.FilePath, Is.EqualTo("virtual.vmd"));
        Assert.That(vmd.Header, Is.Not.Null);
        Assert.That(vmd.MaxFrameCount, Is.Zero);
        Assert.That(_files.Share, Is.EqualTo(FileShare.Read));
        Assert.That(_files.OpenCount, Is.EqualTo(1));
        Assert.That(replacement.OpenCount, Is.Zero);
        Assert.That(_files.LastStream!.IsDisposed, Is.True);
    }

    [Test]
    public void CallerThreadRejectsLoadBeforeOpeningFile()
    {
        RuntimeAssetReadServices.IsCallerThread = true;
        Assert.Throws<NotSupportedException>(() => new VMDFile().Load("virtual.vmd"));
        Assert.That(_files.OpenCount, Is.Zero);
    }

    [Test]
    public void MissingProviderRejectsLoad()
    {
        RuntimeFileDiscoveryServices.Current = null;
        Assert.Throws<NotSupportedException>(() => new VMDFile().Load("virtual.vmd"));
    }

    [Test]
    public void InvalidHeaderDisposesStream()
    {
        _files.Bytes = new byte[74];
        Assert.Throws<InvalidFileError>(() => new VMDFile().Load("invalid.vmd"));
        Assert.That(_files.LastStream!.IsDisposed, Is.True);
    }

    [Test]
    public void DesktopProviderLoadsLocalFile()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, ValidData());
            _files.OpenLocalFile = true;
            var vmd = new VMDFile();
            vmd.Load(path);
            Assert.That(vmd.FilePath, Is.EqualTo(path));
            Assert.That(vmd.Header, Is.Not.Null);
        }
        finally { File.Delete(path); }
    }

    private static byte[] ValidData()
    {
        byte[] bytes = new byte[74];
        Encoding.ASCII.GetBytes("Vocaloid Motion Data 0002").CopyTo(bytes, 0);
        return bytes;
    }

    private sealed class ProbeFiles(byte[] bytes) : IRuntimeFileDiscovery, IRuntimeHostFileReadBackend
    {
        public byte[] Bytes = bytes;
        public int OpenCount;
        public FileShare Share;
        public bool OpenLocalFile;
        public Action? OnOpen;
        public TrackedStream? LastStream;
        public Stream OpenRead(string path, FileShare share)
        {
            OpenCount++;
            Share = share;
            Stream stream = OpenLocalFile ? File.Open(path, FileMode.Open, FileAccess.Read, share) : LastStream = new TrackedStream(Bytes);
            OnOpen?.Invoke();
            return stream;
        }
        public bool FileExists(string path) => File.Exists(path);
        public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);
        public IEnumerable<string> ReadLines(string path) => File.ReadLines(path);
        public IEnumerable<string> EnumerateFiles(string path, string pattern, SearchOption option) => Directory.EnumerateFiles(path, pattern, option);
        public IEnumerable<string> EnumerateDirectories(string path, string pattern, SearchOption option) => Directory.EnumerateDirectories(path, pattern, option);
        public IEnumerable<string> EnumerateFileSystemEntries(string path) => Directory.EnumerateFileSystemEntries(path);
    }

    private sealed class TrackedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool IsDisposed;
        protected override void Dispose(bool disposing) { IsDisposed = true; base.Dispose(disposing); }
    }
}
