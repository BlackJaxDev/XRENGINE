using XREngine.Editor;
using MemoryPack;
using NUnit.Framework;
using XREngine.Core.Files;
using XREngine.Data;

namespace XREngine.UnitTests.Core;

[TestFixture, NonParallelizable]
public sealed class PublishedMetadataHostProbeTests
{
    private IRuntimeFileDiscovery? _previousFiles;
    private IRuntimeAssetReadSource? _previousSource;
    private IFileMappingBackend? _previousMapping;
    private bool _previousCallerThread;
    private EXRRuntimeBuildKind _previousBuildKind;
    private string? _previousConfigPath;
    private string _root = null!;
    private ProbeFiles _files = null!;

    [SetUp]
    public void SetUp()
    {
        _previousFiles = RuntimeFileDiscoveryServices.Current;
        _previousSource = RuntimeAssetReadServices.Source;
        _previousMapping = FileMappingServices.Backend;
        _previousCallerThread = RuntimeAssetReadServices.IsCallerThread;
        _previousBuildKind = XRRuntimeEnvironment.BuildKind;
        _previousConfigPath = XRRuntimeEnvironment.PublishedConfigArchivePath;
        RuntimeAssetReadServices.SetSource(null);
        RuntimeAssetReadServices.IsCallerThread = false;
        XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.Development);
        XRRuntimeEnvironment.ConfigurePublishedPaths(null);
        _root = Path.Combine(Path.GetTempPath(), "published-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _files = new ProbeFiles();
        RuntimeFileDiscoveryServices.Current = _files;
    }

    [TearDown]
    public void TearDown()
    {
        XRRuntimeEnvironment.ConfigurePublishedPaths(null);
        XRRuntimeEnvironment.ConfigureBuildKind(_previousBuildKind);
        RuntimeAssetReadServices.IsCallerThread = _previousCallerThread;
        RuntimeAssetReadServices.SetSource(_previousSource);
        FileMappingServices.Backend = _previousMapping;
        RuntimeFileDiscoveryServices.Current = _previousFiles;
        XRRuntimeEnvironment.ConfigurePublishedPaths(_previousConfigPath);
        Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void MissingArchiveUsesProviderAndKeepsMissingMetadataDiagnostic()
    {
        string path = Path.Combine(_root, "missing.archive");
        FileMappingServices.Backend = null;
        XRRuntimeEnvironment.ConfigurePublishedPaths(path);
        var error = Assert.Throws<InvalidOperationException>(() => AotRuntimeMetadataStore.RequireMetadata());
        Assert.That(error!.Message, Does.Contain(AotRuntimeMetadataStore.MetadataFileName));
        Assert.That(_files.Calls, Is.EqualTo(1));
        Assert.That(File.Exists(path), Is.False);
    }

    [Test]
    public void MissingArchiveWithoutProviderOrMapperReturnsNoMetadata()
    {
        RuntimeFileDiscoveryServices.Current = null;
        FileMappingServices.Backend = null;
        XRRuntimeEnvironment.ConfigurePublishedPaths(Path.Combine(_root, "missing.archive"));
        Assert.That(AotRuntimeMetadataStore.Metadata, Is.Null);
    }

    [Test]
    public void CallerThreadRejectsHostProbeBeforeProviderCall()
    {
        XRRuntimeEnvironment.ConfigurePublishedPaths(Path.Combine(_root, "missing.archive"));
        RuntimeAssetReadServices.IsCallerThread = true;
        Assert.Throws<NotSupportedException>(() => _ = AotRuntimeMetadataStore.Metadata);
        Assert.That(_files.Calls, Is.Zero);
    }

    [Test]
    public void DiscoveryReplacementRejectsProbeResult()
    {
        XRRuntimeEnvironment.ConfigurePublishedPaths(Path.Combine(_root, "missing.archive"));
        _files.OnProbe = () => RuntimeFileDiscoveryServices.Current = new ProbeFiles();
        Assert.Throws<OperationCanceledException>(() => _ = AotRuntimeMetadataStore.Metadata);
    }

    [Test]
    public void SourceReplacementRejectsProbeResult()
    {
        XRRuntimeEnvironment.ConfigurePublishedPaths(Path.Combine(_root, "missing.archive"));
        _files.OnProbe = () => RuntimeAssetReadServices.SetSource(new ProbeSource());
        Assert.Throws<OperationCanceledException>(() => _ = AotRuntimeMetadataStore.Metadata);
    }

    [Test]
    public void PackedArchiveLoadsMetadataThroughDesktopMapping()
    {
        FileMappingServices.Backend = CreateDesktopMappingBackend();
        string staging = Path.Combine(_root, "config");
        Directory.CreateDirectory(staging);
        File.WriteAllBytes(Path.Combine(staging, AotRuntimeMetadataStore.MetadataFileName),
            MemoryPackSerializer.Serialize(new AotRuntimeMetadata { KnownTypeAssemblyQualifiedNames = ["probe-type"] }));
        string archive = Path.Combine(_root, "config.archive");
        AssetArchiveAuthoring.Pack(staging, archive);
        XRRuntimeEnvironment.ConfigurePublishedPaths(archive);
        Assert.That(AotRuntimeMetadataStore.RequireMetadata().KnownTypeAssemblyQualifiedNames, Does.Contain("probe-type"));
        Assert.That(_files.Calls, Is.GreaterThan(0));
    }

    [Test]
    public void PackedArchiveWithoutMetadataKeepsMissingMetadataDiagnostic()
    {
        FileMappingServices.Backend = CreateDesktopMappingBackend();
        string staging = Path.Combine(_root, "empty-config");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "other.txt"), "other");
        string archive = Path.Combine(_root, "empty.archive");
        AssetArchiveAuthoring.Pack(staging, archive);
        XRRuntimeEnvironment.ConfigurePublishedPaths(archive);
        var error = Assert.Throws<InvalidOperationException>(() => AotRuntimeMetadataStore.RequireMetadata());
        Assert.That(error!.Message, Does.Contain(AotRuntimeMetadataStore.MetadataFileName));
    }

    [Test]
    public void CorruptArchiveReportsInvalidData()
    {
        FileMappingServices.Backend = CreateDesktopMappingBackend();
        string archive = Path.Combine(_root, "corrupt.archive");
        File.WriteAllBytes(archive, new byte[64]);
        XRRuntimeEnvironment.ConfigurePublishedPaths(archive);
        Assert.Throws<InvalidDataException>(() => _ = AotRuntimeMetadataStore.Metadata);
    }

    private static IFileMappingBackend CreateDesktopMappingBackend()
    {
        Type type = typeof(CFileMap).Assembly.GetType("XREngine.Runtime.Platform.Desktop.DesktopFileMappingBackend", throwOnError: true)!;
        return (IFileMappingBackend)Activator.CreateInstance(type, nonPublic: true)!;
    }

    private sealed class ProbeFiles : IRuntimeFileDiscovery, IRuntimeHostFileReadBackend
    {
        public int Calls;
        public Action? OnProbe;
        public bool FileExists(string path) { Calls++; bool result = File.Exists(path); OnProbe?.Invoke(); return result; }
        public Stream OpenRead(string path, FileShare share) => File.Open(path, FileMode.Open, FileAccess.Read, share);
        public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);
        public IEnumerable<string> ReadLines(string path) => File.ReadLines(path);
        public IEnumerable<string> EnumerateFiles(string path, string pattern, SearchOption option) => Directory.EnumerateFiles(path, pattern, option);
        public IEnumerable<string> EnumerateDirectories(string path, string pattern, SearchOption option) => Directory.EnumerateDirectories(path, pattern, option);
        public IEnumerable<string> EnumerateFileSystemEntries(string path) => Directory.EnumerateFileSystemEntries(path);
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
