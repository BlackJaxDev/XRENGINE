using System.IO.Compression;
using System.Text;
using NUnit.Framework;
using XREngine.Components.Animation;
using XREngine.Data;

namespace XREngine.UnitTests.Animation;

[TestFixture, NonParallelizable]
public sealed class ImportedHumanoidAvatarProfileHostTests
{
    private IRuntimeAssetReadSource? _previousSource;
    private bool _previousCallerThread;
    private string _root = null!;
    private string _json = null!;

    [SetUp]
    public void SetUp()
    {
        _previousSource = RuntimeAssetReadServices.Source;
        _previousCallerThread = RuntimeAssetReadServices.IsCallerThread;
        RuntimeAssetReadServices.SetSource(null);
        RuntimeAssetReadServices.IsCallerThread = false;
        _root = Path.Combine(Path.GetTempPath(), "avatar-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        string? directory = TestContext.CurrentContext.WorkDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "XREngine.UnitTests/TestData/HumanoidConformance/references/conventional-basic-walk.json.gz")))
            directory = Directory.GetParent(directory)?.FullName;
        if (directory is null) throw new FileNotFoundException("Humanoid conformance reference fixture was not found.");
        using var file = File.OpenRead(Path.Combine(directory, "XREngine.UnitTests/TestData/HumanoidConformance/references/conventional-basic-walk.json.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        _json = reader.ReadToEnd();
    }

    [TearDown]
    public void TearDown()
    {
        RuntimeAssetReadServices.IsCallerThread = _previousCallerThread;
        RuntimeAssetReadServices.SetSource(_previousSource);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CustomSourceMatchesDesktopImportAndDetectsBom(bool utf16)
    {
        string diskPath = Path.Combine(_root, "disk.json");
        File.WriteAllText(diskPath, _json, new UTF8Encoding(false));
        var disk = ImportedHumanoidAvatarProfileImporter.Import(diskPath);
        Encoding encoding = utf16 ? Encoding.Unicode : new UTF8Encoding(false);
        var source = new ProbeSource(encoding.GetPreamble().Concat(encoding.GetBytes(_json)).ToArray());
        RuntimeAssetReadServices.SetSource(source);
        string path = Path.Combine(_root, "virtual.json");
        var imported = ImportedHumanoidAvatarProfileImporter.Import(path);
        Assert.That(disk.NeutralPoseBoneRotations, Is.Not.Empty);
        Assert.That(disk.BoneResponses, Is.Not.Empty);
        Assert.That(imported.NeutralPoseBoneRotations.Count, Is.EqualTo(disk.NeutralPoseBoneRotations.Count));
        Assert.That(imported.BoneResponses.Count, Is.EqualTo(disk.BoneResponses.Count));
        Assert.That(imported.HumanScale, Is.EqualTo(disk.HumanScale));
        Assert.That(imported.SourcePath, Is.EqualTo(Path.GetFullPath(path)));
        Assert.That(source.Reads, Is.EqualTo(1));
        Assert.That(source.Path, Is.EqualTo(Path.GetFullPath(path)));
    }

    [Test]
    public void SourceReplacementDuringReadRejectsProfile()
    {
        var source = new ProbeSource(Encoding.UTF8.GetBytes(_json));
        source.OnRead = () => RuntimeAssetReadServices.SetSource(new ProbeSource([]));
        RuntimeAssetReadServices.SetSource(source);
        Assert.Throws<OperationCanceledException>(() => ImportedHumanoidAvatarProfileImporter.Import(Path.Combine(_root, "profile.json")));
        Assert.That(source.Reads, Is.EqualTo(1));
    }

    [TestCase(true, true, true)]
    [TestCase(false, false, true)]
    [TestCase(false, true, false)]
    public void UnsupportedReadIsRejectedBeforeProviderUse(bool callerThread, bool hostAccess, bool synchronous)
    {
        var source = new ProbeSource([]) { HostAccess = hostAccess, Synchronous = synchronous };
        RuntimeAssetReadServices.SetSource(source);
        RuntimeAssetReadServices.IsCallerThread = callerThread;
        Assert.Throws<NotSupportedException>(() => ImportedHumanoidAvatarProfileImporter.Import(Path.Combine(_root, "profile.json")));
        Assert.That(source.Reads, Is.Zero);
    }

    [Test]
    public void DesktopMissingFileStillThrows()
    {
        Assert.Throws<FileNotFoundException>(() => ImportedHumanoidAvatarProfileImporter.Import(Path.Combine(_root, "missing.json")));
    }

    private sealed class ProbeSource(byte[] bytes) : IRuntimeAssetReadSource
    {
        public bool HostAccess = true;
        public bool Synchronous = true;
        public int Reads;
        public string? Path;
        public Action? OnRead;
        public bool SupportsSynchronousReads => Synchronous;
        public bool SupportsHostFileAccess => HostAccess;
        public bool Exists(string path) => true;
        public byte[] ReadAllBytes(string path) { Reads++; Path = path; OnRead?.Invoke(); return bytes; }
        public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken token = default) => Task.FromResult(ReadAllBytes(path));
    }
}
