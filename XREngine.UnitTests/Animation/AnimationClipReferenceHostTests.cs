using System.Diagnostics.CodeAnalysis;
using NUnit.Framework;
using XREngine;
using XREngine.Animation;
using XREngine.Core.Files;
using XREngine.Data;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine.UnitTests.Animation;

[TestFixture, NonParallelizable]
public sealed class AnimationClipReferenceHostTests
{
    private IRuntimeFileDiscovery? _previousFiles;
    private IRuntimeAssetReadSource? _previousSource;
    private bool _previousCaller;
    private IDisposable _installation = null!;
    private ProbeFiles _files = null!;
    private ProbeServices _services = null!;
    private IDeserializer _deserializer = null!;
    private AssetManager? _manager;

    [SetUp]
    public void SetUp()
    {
        _previousFiles = RuntimeFileDiscoveryServices.Current;
        _previousSource = RuntimeAssetReadServices.Source;
        _previousCaller = RuntimeAssetReadServices.IsCallerThread;
        RuntimeAssetReadServices.SetSource(null);
        RuntimeAssetReadServices.IsCallerThread = false;
        _files = new ProbeFiles();
        RuntimeFileDiscoveryServices.Current = _files;
        _services = new ProbeServices();
        _installation = AssetSerializationServices.Install(new RuntimeAssetSerializationServices(_services));
        _deserializer = new DeserializerBuilder().WithTypeConverter(new AnimationClipYamlTypeConverter()).Build();
    }

    [TearDown]
    public void TearDown()
    {
        _installation.Dispose();
        _manager?.Dispose();
        RuntimeAssetReadServices.IsCallerThread = _previousCaller;
        RuntimeAssetReadServices.SetSource(_previousSource);
        RuntimeFileDiscoveryServices.Current = _previousFiles;
    }

    [Test]
    public void VirtualHostPathUsesCapturedProviderAndDefersLoad()
    {
        Assert.That(Deserialize(_services), Is.SameAs(_services.Clip));
        Assert.That(_files.Calls, Is.EqualTo(1));
        Assert.That(_services.Deferred, Is.EqualTo(1));
        Assert.That(_services.Immediate, Is.Zero);
    }

    [Test]
    public void MissingPathDoesNotLoad()
    {
        _files.Exists = false;
        Assert.That(Deserialize(_services), Is.Null);
        Assert.That(_services.Deferred, Is.Zero);
        Assert.That(_services.Immediate, Is.Zero);
    }

    [Test]
    public void LoadedClipDoesNotProbePath()
    {
        _services.Loaded = true;
        Assert.That(Deserialize(_services), Is.SameAs(_services.Clip));
        Assert.That(_files.Calls, Is.Zero);
    }

    [Test]
    public void NonDeferredReferenceUsesImmediateOwner()
    {
        _services.CanDefer = false;
        Assert.That(Deserialize(_services), Is.SameAs(_services.Clip));
        Assert.That(_services.Immediate, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void OwnerReplacementDuringProbeRejectsReference(bool replaceSource)
    {
        _files.OnProbe = replaceSource
            ? () => RuntimeAssetReadServices.SetSource(new ProbeReadSource())
            : () => RuntimeFileDiscoveryServices.Current = new ProbeFiles();
        YamlException error = Assert.Throws<YamlException>(() => Deserialize(_services))!;
        Assert.That(error.InnerException, Is.InstanceOf<OperationCanceledException>());
        Assert.That(_services.Deferred, Is.Zero);
    }

    [Test]
    public void CallerThreadRejectsHostProbeBeforeProviderUse()
    {
        RuntimeAssetReadServices.IsCallerThread = true;
        YamlException error = Assert.Throws<YamlException>(() => Deserialize(_services))!;
        Assert.That(error.InnerException, Is.InstanceOf<NotSupportedException>());
        Assert.That(_files.Calls, Is.Zero);
    }

    [Test]
    public void CallerThreadUsesEngineCatalogPolicyThroughDecorator()
    {
        var catalog = new ProbeCatalog();
        DirectStorageIO.Source = catalog;
        RuntimeAssetReadServices.IsCallerThread = true;
        _manager = new AssetManager();
        var services = new CatalogServices(new AssetManagerAssetSerializationServices(_manager));
        using IDisposable installation = AssetSerializationServices.Install(new RuntimeAssetSerializationServices(services));
        Assert.That(Deserialize(services), Is.SameAs(services.Clip));
        Assert.That(catalog.Calls, Is.EqualTo(1));
        Assert.That(_files.Calls, Is.Zero);
    }

    private AnimationClip? Deserialize(ProbeServices services)
        => _deserializer.Deserialize<AnimationClip>($"ID: {services.Id}\n");

    private class ProbeServices : IAssetSerializationServices
    {
        public readonly Guid Id = Guid.NewGuid();
        public readonly AnimationClip Clip = new();
        public bool Loaded;
        public bool CanDefer = true;
        public int Deferred;
        public int Immediate;
        public string AssetExtension => "asset";
        public string? GameAssetsPath => null;
        public string? EngineAssetsPath => null;
        public string? CurrentDeserializationPath => null;
        public IReadOnlyList<IYamlTypeConverter> YamlTypeConverters => [];
        public void EnsureYamlAssetRuntimeSupported(string? path = null) { }
        public bool TryGetAssetById(Guid id, [NotNullWhen(true)] out XRAsset? asset) { asset = Loaded ? Clip : null; return asset is not null; }
        public bool TryResolveAssetPathById(Guid id, string? reference, [NotNullWhen(true)] out string? path) { path = id == Id ? "/virtual/clip.asset" : null; return path is not null; }
        public bool TryCreatePortableAssetReference(string path, [NotNullWhen(true)] out string? reference) { reference = null; return false; }
        public XRAsset? LoadImmediate(string path, Type type) { Immediate++; return Clip; }
        public bool TryDeferAssetLoad(string path, Type type, out XRAsset? asset) { Deferred++; asset = CanDefer ? Clip : null; return CanDefer; }
        public bool TryHandleScalarAsset(IParser reader, Type type, Scalar scalar, out object? value) { value = null; return false; }
    }

    private sealed class CatalogServices(IAssetSerializationServices policy) : ProbeServices, IAssetSerializationServices
    {
        public bool AssetPathExists(string path) => policy.AssetPathExists(path);
    }

    private sealed class ProbeFiles : IRuntimeFileDiscovery, IRuntimeHostFileReadBackend
    {
        public bool Exists = true;
        public int Calls;
        public Action? OnProbe;
        public bool FileExists(string path) { Calls++; OnProbe?.Invoke(); return Exists; }
        public Stream OpenRead(string path, FileShare share) => throw new NotSupportedException();
        public byte[] ReadAllBytes(string path) => throw new NotSupportedException();
        public IEnumerable<string> ReadLines(string path) => throw new NotSupportedException();
        public IEnumerable<string> EnumerateFiles(string path, string pattern, SearchOption option) => [];
        public IEnumerable<string> EnumerateDirectories(string path, string pattern, SearchOption option) => [];
        public IEnumerable<string> EnumerateFileSystemEntries(string path) => [];
    }

    private sealed class ProbeReadSource : IRuntimeAssetReadSource
    {
        public bool SupportsSynchronousReads => true;
        public bool SupportsHostFileAccess => true;
        public bool Exists(string path) => throw new NotSupportedException();
        public byte[] ReadAllBytes(string path) => throw new NotSupportedException();
        public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken token = default) => throw new NotSupportedException();
    }

    private sealed class ProbeCatalog : IRuntimeAssetSource, IRuntimeAssetCatalog
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
            entry = path == "/virtual/clip.asset" ? new(path, "clip", RuntimeAssetEncoding.Utf8Text, []) : null;
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
