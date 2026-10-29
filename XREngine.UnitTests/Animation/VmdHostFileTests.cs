using System.Text;
using NUnit.Framework;
using XREngine.Data;
using XREngine.Data.MMD;
using XREngine.Runtime.Platform.Desktop;

namespace XREngine.UnitTests.Animation;

[TestFixture, NonParallelizable]
public sealed class VmdHostFileTests
{
    private IRuntimeFileDiscovery? _previousFiles;
    private IRuntimeAssetReadSource? _previousSource;
    private IHostAssetFileOutput? _previousOutput;
    private bool _previousCallerThread;
    private ProbeFiles _files = null!;

    [SetUp]
    public void SetUp()
    {
        _previousFiles = RuntimeFileDiscoveryServices.Current;
        _previousSource = RuntimeAssetReadServices.Source;
        _previousOutput = HostAssetFileOutputServices.Current;
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
        HostAssetFileOutputServices.Current = _previousOutput;
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

    [Test]
    public void SaveUsesCapturedOutputAndDisposesStream()
    {
        var vmd = new VMDFile();
        vmd.Load("virtual.vmd");
        var replacement = new ProbeOutput();
        var output = new ProbeOutput { OnOpen = () => HostAssetFileOutputServices.Current = replacement };
        HostAssetFileOutputServices.Current = output;
        vmd.Save();
        Assert.That(output.Path, Is.EqualTo("virtual.vmd"));
        Assert.That(output.Opens, Is.EqualTo(1));
        Assert.That(output.LastStream!.IsDisposed, Is.True);
        Assert.That(output.LastStream.LengthWritten, Is.GreaterThan(0));
        Assert.That(replacement.Opens, Is.Zero);
    }

    [Test]
    public void DesktopSavePreservesTailAndDoesNotCreateParent()
    {
        var vmd = new VMDFile();
        vmd.Load("virtual.vmd");
        HostAssetFileOutputServices.Current = new DesktopHostAssetFileOutput();
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vmd-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = System.IO.Path.Combine(root, "existing.vmd");
            File.WriteAllBytes(path, Enumerable.Repeat((byte)0xA5, 128).ToArray());
            vmd.Save(path);
            byte[] bytes = File.ReadAllBytes(path);
            Assert.That(bytes.Length, Is.EqualTo(128));
            Assert.That(bytes[^1], Is.EqualTo(0xA5));
            Assert.That(bytes.AsSpan(0, 25).ToArray(), Is.EqualTo(Encoding.ASCII.GetBytes("Vocaloid Motion Data 0002")));
            string missingParent = System.IO.Path.Combine(root, "missing", "new.vmd");
            Assert.Throws<DirectoryNotFoundException>(() => vmd.Save(missingParent));
            Assert.That(Directory.Exists(System.IO.Path.GetDirectoryName(missingParent)), Is.False);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public void CallerThreadRejectsSaveBeforeOpeningOutput()
    {
        var vmd = new VMDFile();
        vmd.Load("virtual.vmd");
        var output = new ProbeOutput();
        HostAssetFileOutputServices.Current = output;
        RuntimeAssetReadServices.IsCallerThread = true;
        Assert.Throws<NotSupportedException>(() => vmd.Save("virtual.vmd"));
        Assert.That(output.Opens, Is.Zero);
    }

    [Test]
    public void SaveRejectsOutputWithoutOpenWriteCapability()
    {
        var vmd = new VMDFile();
        vmd.Load("virtual.vmd");
        HostAssetFileOutputServices.Current = new BasicOutput();
        Assert.Throws<NotSupportedException>(() => vmd.Save("virtual.vmd"));
    }

    [Test]
    public void SaveRejectsUnloadedAnimationBeforeOpeningOutput()
    {
        var output = new ProbeOutput();
        HostAssetFileOutputServices.Current = output;
        Assert.Throws<InvalidOperationException>(() => new VMDFile().Save("virtual.vmd"));
        Assert.That(output.Opens, Is.Zero);
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

    private class BasicOutput : IHostAssetFileOutput
    {
        public Stream CreateFileWithParentDirectory(string path) => throw new NotSupportedException();
        public void WriteAllText(string path, string text, Encoding encoding) => throw new NotSupportedException();
        public Task WriteAllTextAsync(string path, string text, Encoding encoding) => throw new NotSupportedException();
    }

    private sealed class ProbeOutput : BasicOutput, IHostAssetFileOpenWrite
    {
        public int Opens;
        public string? Path;
        public Action? OnOpen;
        public OutputStream? LastStream;
        public Stream OpenWrite(string path)
        {
            Opens++;
            Path = path;
            var stream = LastStream = new OutputStream();
            OnOpen?.Invoke();
            return stream;
        }
    }

    private sealed class OutputStream : MemoryStream
    {
        public bool IsDisposed;
        public long LengthWritten;
        protected override void Dispose(bool disposing) { LengthWritten = Length; IsDisposed = true; base.Dispose(disposing); }
    }
}
