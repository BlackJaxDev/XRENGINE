using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using NUnit.Framework;
using XREngine.Core.Files;
using XREngine.Data;

namespace XREngine.UnitTests.Core;

[TestFixture, NonParallelizable]
public sealed class YamlAssetPathAdmissionTests
{
    private IRuntimeFileDiscovery? _previousFiles;
    private IRuntimeAssetReadSource? _previousSource;
    private bool _previousCallerThread;
    private string _path = null!;
    private ProbeFiles _files = null!;

    [SetUp]
    public void SetUp()
    {
        _previousFiles = RuntimeFileDiscoveryServices.Current;
        _previousSource = RuntimeAssetReadServices.Source;
        _previousCallerThread = RuntimeAssetReadServices.IsCallerThread;
        _path = Path.GetTempFileName();
        File.WriteAllText(_path, "shader source");
        _files = new ProbeFiles();
        DirectStorageIO.Source = null;
        RuntimeFileDiscoveryServices.Current = _files;
        RuntimeAssetReadServices.IsCallerThread = false;
    }

    [TearDown]
    public void TearDown()
    {
        RuntimeAssetReadServices.IsCallerThread = _previousCallerThread;
        RuntimeAssetReadServices.SetSource(_previousSource);
        RuntimeFileDiscoveryServices.Current = _previousFiles;
        File.Delete(_path);
    }

    [Test]
    public void CallerThreadRejectsHostProbeBeforeCallingProvider()
    {
        RuntimeAssetReadServices.IsCallerThread = true;
        Assert.Throws<NotSupportedException>(() => Exists(_path));
        Assert.That(_files.Calls, Is.Zero);
    }

    [Test]
    public void DesktopUsesHostProviderAndPreservesMissingFileBehavior()
    {
        Assert.That(Exists(_path), Is.True);
        Assert.That(Exists(_path + ".missing"), Is.False);
        Assert.That(_files.Calls, Is.EqualTo(2));
    }

    [Test]
    public void DesktopUsesCustomProviderForVirtualPath()
    {
        _files.Result = true;
        Assert.That(Exists(_path + ".virtual"), Is.True);
        Assert.That(_files.Calls, Is.EqualTo(1));
    }

    [Test]
    public void DesktopWithoutProviderUsesDefaultRawRead()
    {
        RuntimeFileDiscoveryServices.Current = null;
        Assert.That(Exists(_path), Is.True);
        Assert.That(Exists(_path + ".missing"), Is.False);
    }

    [Test]
    public void SourceReplacementDuringProbeRejectsResult()
    {
        _files.OnProbe = () => DirectStorageIO.Source = new Catalog();
        Assert.Throws<OperationCanceledException>(() => Exists(_path));
    }

    [Test]
    public void DiscoveryReplacementDuringProbeRejectsResult()
    {
        _files.OnProbe = () => RuntimeFileDiscoveryServices.Current = new ProbeFiles();
        Assert.Throws<OperationCanceledException>(() => Exists(_path));
    }

    [Test]
    public void DiscoveryReinstallationDuringProbeRejectsResult()
    {
        _files.OnProbe = () =>
        {
            RuntimeFileDiscoveryServices.Current = new ProbeFiles();
            RuntimeFileDiscoveryServices.Current = _files;
        };
        Assert.Throws<OperationCanceledException>(() => Exists(_path));
    }

    [Test]
    public void DiscoveryInstallationDuringFallbackRejectsResult()
    {
        RuntimeFileDiscoveryServices.Current = null;
        RuntimeAssetReadServices.SetSource(new ProbeReadSource(
            () => RuntimeFileDiscoveryServices.Current = new ProbeFiles()));
        Assert.Throws<OperationCanceledException>(() => Exists(_path));
    }

    [Test]
    public void CallerThreadUsesCatalogMetadataWithoutHostProbe()
    {
        var catalog = new Catalog();
        DirectStorageIO.Source = catalog;
        RuntimeAssetReadServices.IsCallerThread = true;
        Assert.That(Exists("catalog/path"), Is.True);
        Assert.That(Exists("catalog/missing"), Is.False);
        Assert.That(catalog.Calls, Is.EqualTo(2));
        Assert.That(_files.Calls, Is.Zero);
    }

    [Test]
    public void DesktopYamlLoadsTextFromAbsolutePath()
    {
        string yaml = "Source: '" + _path.Replace("'", "''") + "'";
        var result = AssetManager.Deserializer.Deserialize<Container>(yaml);
        Assert.That(result.Source?.Text, Is.EqualTo("shader source"));
        Assert.That(_files.Calls, Is.GreaterThan(0));
    }

    [Test]
    public void CallerThreadYamlDoesNotProbeHostFile()
    {
        RuntimeAssetReadServices.IsCallerThread = true;
        string yaml = "Source: '" + _path.Replace("'", "''") + "'";
        Assert.Throws<YamlDotNet.Core.YamlException>(() => AssetManager.Deserializer.Deserialize<Container>(yaml));
        Assert.That(_files.Calls, Is.Zero);
    }

    private static bool Exists(string path)
    {
        MethodInfo method = typeof(XRAssetDeserializer).GetMethod("AssetPathExists", BindingFlags.Static | BindingFlags.NonPublic)!;
        try { return (bool)method.Invoke(null, [path])!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    public sealed class Container { public TextFile? Source { get; set; } }

    private sealed class ProbeReadSource(Action onProbe) : IRuntimeAssetReadSource
    {
        public bool SupportsSynchronousReads => true;
        public bool SupportsHostFileAccess => true;
        public bool Exists(string path) { onProbe(); return true; }
        public byte[] ReadAllBytes(string path) => throw new NotSupportedException();
        public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken token = default) => throw new NotSupportedException();
    }

    private sealed class ProbeFiles : IRuntimeFileDiscovery, IRuntimeHostFileReadBackend
    {
        public int Calls;
        public Action? OnProbe;
        public bool? Result;
        public bool FileExists(string path) { Calls++; bool exists = Result ?? File.Exists(path); OnProbe?.Invoke(); return exists; }
        public Stream OpenRead(string path, FileShare share) => File.Open(path, FileMode.Open, FileAccess.Read, share);
        public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);
        public IEnumerable<string> ReadLines(string path) => File.ReadLines(path);
        public IEnumerable<string> EnumerateFiles(string path, string pattern, SearchOption option) => Directory.EnumerateFiles(path, pattern, option);
        public IEnumerable<string> EnumerateDirectories(string path, string pattern, SearchOption option) => Directory.EnumerateDirectories(path, pattern, option);
        public IEnumerable<string> EnumerateFileSystemEntries(string path) => Directory.EnumerateFileSystemEntries(path);
    }

    private sealed class Catalog : IRuntimeAssetSource, IRuntimeAssetCatalog
    {
        public int Calls;
        public bool SupportsSynchronousReads => false;
        public bool IsAccelerated => false;
        public string Status => "test";
        public IAssetFileSystem? FileSystem => null;
        public string EngineAssetsRoot => "/engine";
        public string GameAssetsRoot => "/game";
        public bool TryGetAsset(string path, [NotNullWhen(true)] out RuntimeAssetCatalogEntry? entry)
        {
            Calls++;
            entry = path == "catalog/path" ? new(path, "text", RuntimeAssetEncoding.Utf8Text, []) : null;
            return entry is not null;
        }
        public bool Exists(string path) => throw new InvalidOperationException("Use catalog metadata.");
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
