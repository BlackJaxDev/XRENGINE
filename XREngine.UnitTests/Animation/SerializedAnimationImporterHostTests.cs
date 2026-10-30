using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using XREngine.Animation;
using XREngine.Animation.Importers;
using XREngine.Data;
using XREngine.Data.Core;

namespace XREngine.UnitTests.Animation;

[TestFixture, NonParallelizable]
public sealed class SerializedAnimationImporterHostTests
{
    private const string ClipYaml = """
AnimationClip:
  serializedVersion: 7
  m_Name: CapturedClip
  m_SampleRate: 30
  m_AnimationClipSettings:
    m_StartTime: 0
    m_StopTime: 1
    m_LoopTime: 0
  m_FloatCurves: []
""";
    private const string ReferenceGuid = "0123456789abcdef0123456789abcdef";

    private IRuntimeAssetReadSource? _previousSource;
    private IRuntimeFileDiscovery? _previousDiscovery;
    private bool _previousCallerThread;
    private ObjectCachePublicationScope _cachePublication = null!;
    private IDisposable? _activeInstallationChange;

    [SetUp]
    public void SetUp()
    {
        _previousSource = RuntimeAssetReadServices.Source;
        _previousDiscovery = RuntimeFileDiscoveryServices.Current;
        _previousCallerThread = RuntimeAssetReadServices.IsCallerThread;
        RuntimeAssetReadServices.SetSource(null);
        RuntimeAssetReadServices.IsCallerThread = false;
        _cachePublication = XRObjectBase.BeginDeferredObjectCachePublication();
    }

    [TearDown]
    public void TearDown()
    {
        _activeInstallationChange?.Dispose();
        _activeInstallationChange = null;
        _cachePublication.Dispose();
        RuntimeAssetReadServices.IsCallerThread = _previousCallerThread;
        RuntimeAssetReadServices.SetSource(_previousSource);
        RuntimeFileDiscoveryServices.Current = _previousDiscovery;
    }

    [Test]
    public void Import_UsesCapturedSourceBytesAndHashesOriginalBom()
    {
        byte[] bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(ClipYaml)).ToArray();
        var source = new ProbeSource(bytes);
        RuntimeAssetReadServices.SetSource(source);

        using AnimationClip clip = AnimYamlImporter.Import(Path.Combine(Path.GetTempPath(), "virtual-clip.anim"));

        Assert.That(clip.SourceImportManifest?.SourceIdentity.SourceContentSha256,
            Is.EqualTo(Convert.ToHexString(SHA256.HashData(bytes))));
        Assert.That(source.Reads, Is.EqualTo(1));
    }

    [Test]
    public void Import_RejectsSourceRetirementDuringRead()
    {
        var source = new ProbeSource(Encoding.UTF8.GetBytes(ClipYaml));
        source.OnRead = () => RuntimeAssetReadServices.SetSource(new ProbeSource([]));
        RuntimeAssetReadServices.SetSource(source);

        Assert.Throws<OperationCanceledException>(() =>
            AnimYamlImporter.Import(Path.Combine(Path.GetTempPath(), "virtual-clip.anim")));
        Assert.That(source.Reads, Is.EqualTo(1));
    }

    [Test]
    public void Import_RejectsDiscoveryRetirementDuringRead()
    {
        var source = new ProbeSource(Encoding.UTF8.GetBytes(ClipYaml));
        source.OnRead = () => RuntimeFileDiscoveryServices.Current = new ProbeDiscovery();
        RuntimeAssetReadServices.SetSource(source);

        Assert.Throws<OperationCanceledException>(() =>
            AnimYamlImporter.Import(Path.Combine(Path.GetTempPath(), "virtual-clip.anim")));
        Assert.That(source.Reads, Is.EqualTo(1));
    }

    [Test]
    public void Import_AllowsCompletedUnrelatedHostInstallationChange()
    {
        var source = new ProbeSource(Encoding.UTF8.GetBytes(ClipYaml));
        source.OnRead = () =>
        {
            using IDisposable change = RuntimeSourcePublicationServices.BeginInstallationChange();
        };
        RuntimeAssetReadServices.SetSource(source);

        using AnimationClip clip = AnimYamlImporter.Import(Path.Combine(Path.GetTempPath(), "virtual-clip.anim"));

        Assert.That(clip.SourceImportManifest, Is.Not.Null);
        Assert.That(source.Reads, Is.EqualTo(1));
    }

    [Test]
    public void Import_RejectsPublicationWhileUnrelatedInstallationChangeIsActive()
    {
        var source = new ProbeSource(Encoding.UTF8.GetBytes(ClipYaml));
        source.OnRead = () => _activeInstallationChange = RuntimeSourcePublicationServices.BeginInstallationChange();
        RuntimeAssetReadServices.SetSource(source);

        Assert.Throws<OperationCanceledException>(() =>
            AnimYamlImporter.Import(Path.Combine(Path.GetTempPath(), "virtual-clip.anim")));
        Assert.That(source.Reads, Is.EqualTo(1));
    }

    [Test]
    public void Import_AdditiveReferenceUsesOneSourceForHeaderMetadataAndRecursiveClip()
    {
        (string rootPath, string childPath, string metaPath) = ProjectPaths();
        var source = CreateAdditiveSource(rootPath, childPath, metaPath);
        RuntimeFileDiscoveryServices.Current = new ProbeDiscovery(metaPath);
        RuntimeAssetReadServices.SetSource(source);

        using AnimationClip clip = AnimYamlImporter.Import(rootPath);
        using AnimationClip child = clip.ImportedAdditiveReferencePoseClip!;

        Assert.That(child, Is.Not.Null);
        Assert.That(child, Is.Not.SameAs(clip));
        Assert.That(source.ReadPaths, Is.EqualTo(new[] { rootPath, metaPath, childPath, childPath }));
        Assert.That(child.SourceImportManifest?.SourceIdentity.SourceContentSha256,
            Is.EqualTo(Convert.ToHexString(SHA256.HashData(source.Files[childPath]))));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Import_RejectsDiscoveryRetirementDuringMetadataEnumeration(bool failEnumeration)
    {
        (string rootPath, string childPath, string metaPath) = ProjectPaths();
        var source = CreateAdditiveSource(rootPath, childPath, metaPath);
        var discovery = new ProbeDiscovery(metaPath)
        {
            OnEnumerate = () => RuntimeFileDiscoveryServices.Current = new ProbeDiscovery(metaPath),
            FailEnumeration = failEnumeration,
        };
        RuntimeFileDiscoveryServices.Current = discovery;
        RuntimeAssetReadServices.SetSource(source);

        Assert.Throws<OperationCanceledException>(() => AnimYamlImporter.Import(rootPath));
        Assert.That(source.ReadPaths, Is.EqualTo(new[] { rootPath }));
    }

    [Test]
    public void Import_RejectsSourceRetirementDuringAdditiveMetadataRead()
    {
        (string rootPath, string childPath, string metaPath) = ProjectPaths();
        var source = CreateAdditiveSource(rootPath, childPath, metaPath);
        source.OnRead = path =>
        {
            if (path == metaPath)
                RuntimeAssetReadServices.SetSource(new ProbeSource([]));
        };
        RuntimeFileDiscoveryServices.Current = new ProbeDiscovery(metaPath);
        RuntimeAssetReadServices.SetSource(source);

        Assert.Throws<OperationCanceledException>(() => AnimYamlImporter.Import(rootPath));
        Assert.That(source.ReadPaths, Is.EqualTo(new[] { rootPath, metaPath }));
    }

    [Test]
    public void Import_RejectsDiscoveryRetirementAfterRecursiveClipConstruction()
    {
        (string rootPath, string childPath, string metaPath) = ProjectPaths();
        var source = CreateAdditiveSource(rootPath, childPath, metaPath);
        source.OnRead = path =>
        {
            if (path == childPath && source.ReadPaths.Count == 4)
                RuntimeFileDiscoveryServices.Current = new ProbeDiscovery(metaPath);
        };
        RuntimeFileDiscoveryServices.Current = new ProbeDiscovery(metaPath);
        RuntimeAssetReadServices.SetSource(source);

        Assert.Throws<OperationCanceledException>(() => AnimYamlImporter.Import(rootPath));
        Assert.That(source.ReadPaths, Is.EqualTo(new[] { rootPath, metaPath, childPath, childPath }));
    }

    [TestCase(true, true, true)]
    [TestCase(false, false, true)]
    [TestCase(false, true, false)]
    public void Import_RejectsUnavailableHostCapabilityBeforeReading(bool callerThread, bool hostAccess, bool synchronous)
    {
        var source = new ProbeSource(Encoding.UTF8.GetBytes(ClipYaml))
        {
            HostAccess = hostAccess,
            Synchronous = synchronous,
        };
        RuntimeAssetReadServices.SetSource(source);
        RuntimeAssetReadServices.IsCallerThread = callerThread;

        Assert.Throws<NotSupportedException>(() =>
            AnimYamlImporter.Import(Path.Combine(Path.GetTempPath(), "virtual-clip.anim")));
        Assert.That(source.Reads, Is.Zero);
    }

    private sealed class ProbeSource(byte[] bytes) : IRuntimeAssetReadSource
    {
        public bool HostAccess = true;
        public bool Synchronous = true;
        public int Reads;
        public Action? OnRead;
        public bool SupportsSynchronousReads => Synchronous;
        public bool SupportsHostFileAccess => HostAccess;
        public bool Exists(string path) => true;
        public byte[] ReadAllBytes(string path)
        {
            Reads++;
            OnRead?.Invoke();
            return bytes;
        }
        public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult(ReadAllBytes(path));
    }

    private sealed class ProbeDiscovery : IRuntimeFileDiscovery
    {
        private readonly string? _metaPath;
        public ProbeDiscovery(string? metaPath = null) => _metaPath = metaPath;
        public Action? OnEnumerate;
        public bool FailEnumeration;
        public IEnumerable<string> EnumerateFiles(string path, string pattern, SearchOption searchOption)
        {
            OnEnumerate?.Invoke();
            if (FailEnumeration)
                throw new IOException("Metadata enumeration failed.");
            if (_metaPath is not null)
                yield return _metaPath;
        }
        public IEnumerable<string> EnumerateDirectories(string path, string pattern, SearchOption searchOption) => [];
        public IEnumerable<string> EnumerateFileSystemEntries(string path) => [];
    }

    private static (string Root, string Child, string Meta) ProjectPaths()
    {
        string assets = Path.Combine(Path.GetTempPath(), "animation-owner-project", "Assets");
        string child = Path.Combine(assets, "reference.anim");
        return (Path.Combine(assets, "root.anim"), child, child + ".meta");
    }

    private static MappedSource CreateAdditiveSource(string rootPath, string childPath, string metaPath)
    {
        string rootYaml = ClipYaml.Replace(
            "    m_LoopTime: 0",
            "    m_LoopTime: 0\n    m_HasAdditiveReferencePose: 1\n" +
            $"    m_AdditiveReferencePoseClip: {{fileID: 7400000, guid: {ReferenceGuid}, type: 2}}\n" +
            "    m_AdditiveReferencePoseTime: 0",
            StringComparison.Ordinal);
        string childYaml = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!74 &7400000\n" + ClipYaml;
        return new MappedSource(new Dictionary<string, byte[]>
        {
            [rootPath] = Encoding.UTF8.GetBytes(rootYaml),
            [childPath] = Encoding.UTF8.GetBytes(childYaml),
            [metaPath] = Encoding.UTF8.GetBytes($"fileFormatVersion: 2\nguid: {ReferenceGuid}\n"),
        });
    }

    private sealed class MappedSource(Dictionary<string, byte[]> files) : IRuntimeAssetReadSource
    {
        public Dictionary<string, byte[]> Files { get; } = files;
        public List<string> ReadPaths { get; } = [];
        public Action<string>? OnRead;
        public bool SupportsSynchronousReads => true;
        public bool SupportsHostFileAccess => true;
        public bool Exists(string path) => Files.ContainsKey(path);
        public byte[] ReadAllBytes(string path)
        {
            ReadPaths.Add(path);
            OnRead?.Invoke(path);
            return Files[path];
        }
        public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult(ReadAllBytes(path));
    }
}
