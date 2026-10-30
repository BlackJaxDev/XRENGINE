using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using NUnit.Framework;
using XREngine.Components.Animation;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Animation;

[TestFixture, NonParallelizable]
public sealed class HumanoidConformanceFileReadTests
{
    private IRuntimeFileDiscovery? _previousDiscovery;
    private IRuntimeAssetReadSource? _previousSource;
    private bool _previousCaller;
    private ProbeFiles _files = null!;
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _previousDiscovery = RuntimeFileDiscoveryServices.Current;
        _previousSource = RuntimeAssetReadServices.Source;
        _previousCaller = RuntimeAssetReadServices.IsCallerThread;
        RuntimeAssetReadServices.SetSource(null);
        RuntimeAssetReadServices.IsCallerThread = false;
        _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "conformance-read-" + Guid.NewGuid().ToString("N")));
        _files = new ProbeFiles(_root);
        RuntimeFileDiscoveryServices.Current = _files;
    }

    [TearDown]
    public void TearDown()
    {
        RuntimeAssetReadServices.IsCallerThread = _previousCaller;
        RuntimeAssetReadServices.SetSource(_previousSource);
        RuntimeFileDiscoveryServices.Current = _previousDiscovery;
    }

    [Test]
    public void ManifestHashesSourceWithCapturedProvider()
    {
        string sourcePath = Path.Combine(_root, "source.bin");
        byte[] source = [1, 2, 3, 4];
        _files.Content[sourcePath] = source;
        var manifest = new HumanoidConformanceManifest
        {
            CorpusId = "sample", CorpusVersion = "1", Provenance = "sample",
            SourceFiles = [new HumanoidConformanceSourceFile
            {
                Id = "source", RelativePath = "source.bin", Sha256 = Convert.ToHexString(SHA256.HashData(source)),
                Provenance = "sample", Signature = "sample", ArtifactKind = HumanoidConformanceArtifactKind.KnownAnswerReference,
            }],
        };
        _files.Content[Path.Combine(_root, "manifest.json")] = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(manifest));

        HumanoidConformanceValidationResult result = HumanoidConformanceManifestLoader.LoadAndValidate(Path.Combine(_root, "manifest.json"));

        Assert.That(result.Issues.Any(x => x.Code is "ManifestMissing" or "SourceFileMissing" or "SourceFileHashChanged"), Is.False);
        Assert.That(_files.Opens, Is.EqualTo(2));
        Assert.That(_files.LastShare, Is.EqualTo(FileShare.Read));
    }

    [Test]
    public void ManifestRejectsProviderReplacementDuringSourceRead()
    {
        string sourcePath = Path.Combine(_root, "source.bin");
        byte[] source = [9, 8, 7];
        _files.Content[sourcePath] = source;
        var manifest = new HumanoidConformanceManifest
        {
            CorpusId = "sample", CorpusVersion = "1", Provenance = "sample",
            SourceFiles = [new HumanoidConformanceSourceFile
            {
                Id = "source", RelativePath = "source.bin", Sha256 = Convert.ToHexString(SHA256.HashData(source)),
                Provenance = "sample", Signature = "sample", ArtifactKind = HumanoidConformanceArtifactKind.KnownAnswerReference,
            }],
        };
        _files.Content[Path.Combine(_root, "manifest.json")] = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(manifest));
        _files.OnOpen = path => { if (path == sourcePath) RuntimeFileDiscoveryServices.Current = new ProbeFiles(_root); };

        Assert.Throws<OperationCanceledException>(() => HumanoidConformanceManifestLoader.LoadAndValidate(Path.Combine(_root, "manifest.json")));
    }

    [Test]
    public void ManifestMissingAndUnreadableKeepTheirIssueCodes()
    {
        string path = Path.Combine(_root, "manifest.json");
        Assert.That(HumanoidConformanceManifestLoader.LoadAndValidate(path).Issues.Any(x => x.Code == "ManifestMissing"), Is.True);
        _files.Content[path] = Encoding.UTF8.GetBytes("{}");
        _files.FailOpen = path;
        Assert.That(HumanoidConformanceManifestLoader.LoadAndValidate(path).Issues.Any(x => x.Code == "ManifestUnreadable"), Is.True);
    }

    [Test]
    public void DirectManifestValidationRejectsReplacementDuringHashRead()
    {
        string path = Path.Combine(_root, "source.bin");
        byte[] bytes = [1, 2, 3];
        _files.Content[path] = bytes;
        var manifest = new HumanoidConformanceManifest
        {
            CorpusId = "sample", CorpusVersion = "1", Provenance = "sample",
            SourceFiles = [new HumanoidConformanceSourceFile
            {
                Id = "source", RelativePath = "source.bin", Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                Provenance = "sample", Signature = "sample", ArtifactKind = HumanoidConformanceArtifactKind.KnownAnswerReference,
            }],
        };
        _files.OnRead = readPath => { if (readPath == path) RuntimeFileDiscoveryServices.Current = new ProbeFiles(_root); };

        Assert.Throws<OperationCanceledException>(() => HumanoidConformanceManifestLoader.Validate(manifest, _root));
    }

    [Test]
    public void ManifestReportsChangedSourceHash()
    {
        string path = Path.Combine(_root, "source.bin");
        _files.Content[path] = [1, 2, 3];
        var manifest = new HumanoidConformanceManifest
        {
            CorpusId = "sample", CorpusVersion = "1", Provenance = "sample",
            SourceFiles = [new HumanoidConformanceSourceFile
            {
                Id = "source", RelativePath = "source.bin", Sha256 = new string('0', 64),
                Provenance = "sample", Signature = "sample", ArtifactKind = HumanoidConformanceArtifactKind.KnownAnswerReference,
            }],
        };

        Assert.That(HumanoidConformanceManifestLoader.Validate(manifest, _root).Issues.Any(x => x.Code == "SourceFileHashChanged"), Is.True);
    }

    [Test]
    public void MappingCorrectionUsesObservedFbxHashOnce()
    {
        (string sidecarPath, string fbxPath, HumanoidComponent humanoid) = PrepareValidCorrection();

        HumanoidConformanceMappingCorrectionResult result = HumanoidConformanceMappingCorrectionLoader.LoadValidateAndApply(
            sidecarPath, fbxPath, humanoid);

        Assert.That(result.Applied, Is.True, string.Join("; ", result.Issues.Select(x => x.Code + ": " + x.Message)));
        Assert.That(humanoid.AvatarDefinition.SourceModelContentSha256, Is.EqualTo(Convert.ToHexString(SHA256.HashData(_files.Content[fbxPath]))));
        Assert.That(_files.Opens, Is.EqualTo(2));
    }

    private (string SidecarPath, string FbxPath, HumanoidComponent Humanoid) PrepareValidCorrection()
    {
        string fbxPath = Path.Combine(_root, "source.fbx");
        byte[] source = [4, 5, 6];
        _files.Content[fbxPath] = source;
        string hash = Convert.ToHexString(SHA256.HashData(source));
        (HumanoidComponent seed, Dictionary<EHumanoidAvatarBoneRole, SceneNode?> seedMappings, Dictionary<string, string> roles) = CreateManagedHumanoid();
        seed.SetSourceModelContentSha256(hash);
        seed.SetAvatarBoneMappings(seedMappings);
        Assert.That(seed.ConfirmAvatarDefinition(out string diagnostic), Is.True, diagnostic);
        string expectedSignature = seed.AvatarDefinition.DefinitionContentSha256;

        string sidecarPath = Path.Combine(_root, "correction.json");
        _files.Content[sidecarPath] = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new HumanoidConformanceMappingCorrection
        {
            Fixture = "managed-skeleton", FixtureVersion = "1", MappingMode = "persisted-corrections", RootPath = ".",
            SourceFbxSha256 = hash, ExpectedAvatarDefinitionSignature = expectedSignature, Roles = roles,
        }));
        HumanoidComponent target = CreateManagedHumanoid().Humanoid;
        return (sidecarPath, fbxPath, target);
    }

    private static (HumanoidComponent Humanoid, Dictionary<EHumanoidAvatarBoneRole, SceneNode?> Mappings, Dictionary<string, string> Roles) CreateManagedHumanoid()
    {
        var root = new SceneNode("Root", new Transform());
        var hips = new SceneNode(root, "Hips", new Transform());
        var spine = new SceneNode(hips, "Spine", new Transform(translation: new(0.0f, 0.3f, 0.0f)));
        var chest = new SceneNode(spine, "Chest", new Transform(translation: new(0.0f, 0.3f, 0.0f)));
        var neck = new SceneNode(chest, "Neck", new Transform(translation: new(0.0f, 0.12f, 0.0f)));
        _ = new SceneNode(neck, "Head", new Transform(translation: new(0.0f, 0.16f, 0.0f)));
        var leftShoulder = new SceneNode(chest, "LeftShoulder", new Transform(translation: new(-0.25f, 0.1f, 0.0f)));
        var leftUpperArm = new SceneNode(leftShoulder, "LeftUpperArm", new Transform(translation: new(-0.3f, 0.0f, 0.0f)));
        var leftLowerArm = new SceneNode(leftUpperArm, "LeftLowerArm", new Transform(translation: new(-0.3f, 0.0f, 0.0f)));
        _ = new SceneNode(leftLowerArm, "LeftHand", new Transform(translation: new(-0.2f, 0.0f, 0.0f)));
        var rightShoulder = new SceneNode(chest, "RightShoulder", new Transform(translation: new(0.25f, 0.1f, 0.0f)));
        var rightUpperArm = new SceneNode(rightShoulder, "RightUpperArm", new Transform(translation: new(0.3f, 0.0f, 0.0f)));
        var rightLowerArm = new SceneNode(rightUpperArm, "RightLowerArm", new Transform(translation: new(0.3f, 0.0f, 0.0f)));
        _ = new SceneNode(rightLowerArm, "RightHand", new Transform(translation: new(0.2f, 0.0f, 0.0f)));
        var leftUpperLeg = new SceneNode(hips, "LeftUpperLeg", new Transform(translation: new(-0.2f, -0.4f, 0.0f)));
        var leftLowerLeg = new SceneNode(leftUpperLeg, "LeftLowerLeg", new Transform(translation: new(0.0f, -0.35f, 0.0f)));
        _ = new SceneNode(leftLowerLeg, "LeftFoot", new Transform(translation: new(0.0f, -0.25f, 0.0f)));
        var rightUpperLeg = new SceneNode(hips, "RightUpperLeg", new Transform(translation: new(0.2f, -0.4f, 0.0f)));
        var rightLowerLeg = new SceneNode(rightUpperLeg, "RightLowerLeg", new Transform(translation: new(0.0f, -0.35f, 0.0f)));
        _ = new SceneNode(rightLowerLeg, "RightFoot", new Transform(translation: new(0.0f, -0.25f, 0.0f)));
        SaveBindPose(root);
        HumanoidComponent humanoid;
        using (HumanoidComponent.BeginDeferredSceneNodeInitialization())
            humanoid = root.AddComponent<HumanoidComponent>()!;
        humanoid.InitializeSceneNodeBindings();
        var mappings = new Dictionary<EHumanoidAvatarBoneRole, SceneNode?>();
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        CollectRoles(root, string.Empty, mappings, roles);
        return (humanoid, mappings, roles);
    }

    private static void CollectRoles(SceneNode parent, string prefix,
        Dictionary<EHumanoidAvatarBoneRole, SceneNode?> mappings, Dictionary<string, string> roles)
    {
        foreach (var childTransform in parent.Transform.Children)
        {
            if (childTransform.SceneNode is not { } child)
                continue;
            string name = child.Name ?? throw new InvalidOperationException("The test skeleton requires named bones.");
            string path = string.IsNullOrEmpty(prefix) ? name : prefix + "/" + name;
            if (Enum.TryParse(name, ignoreCase: false, out EHumanoidAvatarBoneRole role))
            {
                mappings.Add(role, child);
                roles.Add(role.ToString(), path);
            }
            CollectRoles(child, path, mappings, roles);
        }
    }

    private static void SaveBindPose(SceneNode node)
    {
        node.Transform.SaveBindState();
        foreach (var child in node.Transform.Children)
            if (child.SceneNode is not null)
                SaveBindPose(child.SceneNode);
    }

    [Test]
    public void MappingCorrectionRejectsProviderReplacementBeforeApply()
    {
        (string sidecarPath, string fbxPath, HumanoidComponent humanoid) = PrepareValidCorrection();
        _files.OnRead = path => { if (path == fbxPath) RuntimeFileDiscoveryServices.Current = new ProbeFiles(_root); };

        Assert.Throws<OperationCanceledException>(() => HumanoidConformanceMappingCorrectionLoader.LoadValidateAndApply(
            sidecarPath, fbxPath, humanoid));
        Assert.That(humanoid.AvatarDefinition.SourceModelContentSha256, Is.Empty);
    }

    [Test]
    public void MappingCorrectionMissingAndUnreadableKeepTheirIssueCodes()
    {
        string sidecarPath = Path.Combine(_root, "correction.json");
        string fbxPath = Path.Combine(_root, "source.fbx");
        Assert.That(HumanoidConformanceMappingCorrectionLoader.LoadValidateAndApply(
            sidecarPath, fbxPath, new HumanoidComponent()).Issues.Any(x => x.Code == "SidecarMissing"), Is.True);
        _files.Content[sidecarPath] = Encoding.UTF8.GetBytes("{}");
        _files.FailOpen = sidecarPath;
        Assert.That(HumanoidConformanceMappingCorrectionLoader.LoadValidateAndApply(
            sidecarPath, fbxPath, new HumanoidComponent()).Issues.Any(x => x.Code == "SidecarUnreadable"), Is.True);
    }

    [Test]
    public void MappingCorrectionReportsFbxHashMismatch()
    {
        string sidecarPath = Path.Combine(_root, "correction.json");
        string fbxPath = Path.Combine(_root, "source.fbx");
        _files.Content[fbxPath] = [4, 5, 6];
        _files.Content[sidecarPath] = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new HumanoidConformanceMappingCorrection
        {
            Fixture = "sample", FixtureVersion = "1", MappingMode = "persisted-corrections", RootPath = ".",
            SourceFbxSha256 = new string('0', 64), ExpectedAvatarDefinitionSignature = "expected",
        }));

        Assert.That(HumanoidConformanceMappingCorrectionLoader.LoadValidateAndApply(
            sidecarPath, fbxPath, new HumanoidComponent()).Issues.Any(x => x.Code == "SourceFbxHashMismatch"), Is.True);
    }

    [Test]
    public void HostCapableSourceReservesDiscoveryDuringCorrectionPublication()
    {
        RuntimeAssetReadServices.SetSource(new HostSource());
        using var owner = new HumanoidConformanceFileRead("Mapping correction publication test");
        var replacement = new ProbeFiles(_root);

        using (owner.BeginPublication())
            Assert.Throws<InvalidOperationException>(() => RuntimeFileDiscoveryServices.Current = replacement);

        RuntimeFileDiscoveryServices.Current = replacement;
        Assert.Throws<OperationCanceledException>(() => owner.EnsureCurrent());
        using var retry = new HumanoidConformanceFileRead("Mapping correction publication retry");
        using IDisposable publication = retry.BeginPublication();
    }

    [Test]
    public void CompletedUnrelatedHostChangeDoesNotRetireCapturedFileOwner()
    {
        RuntimeAssetReadServices.SetSource(new HostSource());
        using var owner = new HumanoidConformanceFileRead("Mapping correction publication test");
        using (RuntimeSourcePublicationServices.BeginInstallationChange()) { }

        owner.EnsureCurrent();
        using IDisposable publication = owner.BeginPublication();
    }

    private sealed class ProbeFiles(string root) : IRuntimeFileDiscovery, IRuntimeHostFileReadBackend, IAssetMetadataFileBackend
    {
        public Dictionary<string, byte[]> Content { get; } = new(StringComparer.Ordinal);
        public Action<string>? OnOpen;
        public Action<string>? OnRead;
        public string? FailOpen;
        public int Opens;
        public FileShare LastShare;
        public bool FileExists(string path) => Content.ContainsKey(path);
        public bool DirectoryExists(string path) => path == Path.Combine(root, ".git");
        public Stream OpenRead(string path, FileShare share)
        {
            Opens++;
            LastShare = share;
            if (path == FailOpen) throw new IOException("expected read failure");
            Stream stream = new ProbeStream(Content[path], () => OnRead?.Invoke(path));
            OnOpen?.Invoke(path);
            return stream;
        }
        public byte[] ReadAllBytes(string path) => throw new NotSupportedException();
        public IEnumerable<string> ReadLines(string path) => throw new NotSupportedException();
        public IEnumerable<string> EnumerateFiles(string path, string pattern, SearchOption option) => [];
        public IEnumerable<string> EnumerateDirectories(string path, string pattern, SearchOption option) => [];
        public IEnumerable<string> EnumerateFileSystemEntries(string path) => [];
        public void CreateDirectory(string path) => throw new NotSupportedException();
        public void DeleteDirectory(string path) => throw new NotSupportedException();
        public void DeleteFile(string path) => throw new NotSupportedException();
        public void MoveFile(string source, string destination, bool overwrite) => throw new NotSupportedException();
        public void CopyFile(string source, string destination, bool overwrite) => throw new NotSupportedException();
        public string ReadAllText(string path) => throw new NotSupportedException();
        public void WriteAllText(string path, string contents) => throw new NotSupportedException();
        public DateTime GetLastWriteTimeUtc(string path) => throw new NotSupportedException();
        public Guid TryExtractAssetGuid(string path) => throw new NotSupportedException();
    }

    private sealed class ProbeStream(byte[] bytes, Action onRead) : MemoryStream(bytes, writable: false)
    {
        private bool _read;
        private void AfterRead()
        {
            if (_read) return;
            _read = true;
            onRead();
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int length = base.Read(buffer, offset, count);
            AfterRead();
            return length;
        }
        public override int Read(Span<byte> buffer)
        {
            int length = base.Read(buffer);
            AfterRead();
            return length;
        }
    }

    private sealed class HostSource : IRuntimeAssetReadSource
    {
        public bool SupportsSynchronousReads => true;
        public bool SupportsHostFileAccess => true;
        public bool Exists(string path) => throw new NotSupportedException();
        public byte[] ReadAllBytes(string path) => throw new NotSupportedException();
        public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken token = default) => throw new NotSupportedException();
    }
}
