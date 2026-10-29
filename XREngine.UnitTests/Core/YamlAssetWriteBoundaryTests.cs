using System.Diagnostics.CodeAnalysis;
using System.Text;
using NUnit.Framework;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Core;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine.UnitTests.Core;

[TestFixture, NonParallelizable]
public sealed class YamlAssetWriteBoundaryTests
{
    private IRuntimeFileDiscovery? _previousFiles;
    private IRuntimeAssetReadSource? _previousSource;
    private bool _previousCaller;
    private IDisposable _cacheSuppression = null!;
    private IDisposable _ownerInstallation = null!;
    private IDisposable? _replacementInstallation;
    private Files _files = null!;
    private Services _services = null!;
    private string _path = null!;

    [SetUp]
    public void SetUp()
    {
        _previousFiles = RuntimeFileDiscoveryServices.Current;
        _previousSource = RuntimeAssetReadServices.Source;
        _previousCaller = RuntimeAssetReadServices.IsCallerThread;
        _cacheSuppression = XRObjectBase.SuppressObjectCacheRegistration();
        RuntimeAssetReadServices.SetSource(null);
        RuntimeAssetReadServices.IsCallerThread = false;
        _path = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.WorkDirectory, "yaml-write", "source.slang"));
        _files = new Files { Bytes = Encoding.UTF8.GetBytes("shader source") };
        RuntimeFileDiscoveryServices.Current = _files;
        _services = new Services { EngineAssetsPath = Path.GetDirectoryName(_path) };
        _ownerInstallation = AssetSerializationServices.Install(_services);
    }

    [TearDown]
    public void TearDown()
    {
        _replacementInstallation?.Dispose();
        _replacementInstallation = null;
        _ownerInstallation.Dispose();
        RuntimeAssetReadServices.IsCallerThread = _previousCaller;
        RuntimeAssetReadServices.SetSource(_previousSource);
        RuntimeFileDiscoveryServices.Current = _previousFiles;
        _cacheSuppression.Dispose();
    }

    [TestCase(65001, false)]
    [TestCase(65001, true)]
    [TestCase(1200, true)]
    [TestCase(1201, true)]
    [TestCase(12000, true)]
    [TestCase(12001, true)]
    [TestCase(65000, false)]
    public void UnchangedBackingTextUsesPortablePathWithExistingBomSemantics(int codePage, bool preamble)
    {
        // A literal UTF-7 fixture avoids the runtime ban on GetEncoding(65000).
        // Its marker remains text under the legacy StreamReader decoding contract.
        string text = codePage == 65000 ? "\uFEFFshader π" : "shader π";
        if (codePage == 65000)
            _files.Bytes = Encoding.ASCII.GetBytes("+/v8-shader +A8A-");
        else
        {
            Encoding encoding = Encoding.GetEncoding(codePage);
            _files.Bytes = preamble ? [.. encoding.GetPreamble(), .. encoding.GetBytes(text)] : encoding.GetBytes(text);
        }
        var raw = new RawSource { ThrowOnRead = true };
        RuntimeAssetReadServices.SetSource(raw);

        string yaml = Serialize(CreateText(text));

        Assert.That(yaml, Does.Contain("Source: source.slang"));
        Assert.That(yaml, Does.Not.Contain("Text:"));
        Assert.That(_files.Probes, Is.EqualTo(1));
        Assert.That(_files.Reads, Is.EqualTo(1));
        Assert.That(raw.Reads, Is.Zero, "The host provider must supply both encoding and content bytes.");
    }

    [Test]
    public void ChangedBackingTextRemainsInline()
    {
        string yaml = Serialize(CreateText("edited shader"));
        Assert.That(yaml, Does.Contain("Text: edited shader"));
        Assert.That(yaml, Does.Not.Contain("Source: source.slang"));
    }

    [Test]
    public void MissingBackingTextRemainsInlineWithoutReading()
    {
        _files.Exists = false;
        string yaml = Serialize(CreateText("shader source"));
        Assert.That(yaml, Does.Contain("Text: shader source"));
        Assert.That(_files.Reads, Is.Zero);
    }

    [Test]
    public void UnreadableBackingTextRemainsInline()
    {
        _files.OnRead = () => throw new IOException("unreadable test source");
        Assert.That(Serialize(CreateText("shader source")), Does.Contain("Text: shader source"));
    }

    [TestCase(true, true)]
    [TestCase(false, false)]
    public void UnsupportedHostKeepsTextInlineWithoutFileProbes(bool callerThread, bool hostAccess)
    {
        RuntimeAssetReadServices.SetSource(new RawSource { HostAccess = hostAccess });
        RuntimeAssetReadServices.IsCallerThread = callerThread;
        Assert.That(Serialize(CreateText("shader source")), Does.Contain("Text: shader source"));
        Assert.That(_files.Probes, Is.Zero);
        Assert.That(_files.Reads, Is.Zero);
    }

    [TestCase(true, true)]
    [TestCase(false, false)]
    public void UnsupportedBackingComparisonKeepsEditedSavedAssetInline(bool callerThread, bool hostAccess)
    {
        RuntimeAssetReadServices.SetSource(new RawSource { HostAccess = hostAccess });
        RuntimeAssetReadServices.IsCallerThread = callerThread;
        using TextFile text = CreateSavedText("edited shader");

        string yaml = Serialize(text);

        Assert.That(yaml, Does.Contain("Text: edited shader"));
        Assert.That(yaml, Does.Not.Contain("Path: original.asset"));
        Assert.That(_services.Probes, Is.Zero);
        Assert.That(_files.Probes + _files.Reads, Is.Zero);
    }

    [Test]
    public void MissingHostReadProviderKeepsEditedSavedAssetInline()
    {
        RuntimeFileDiscoveryServices.Current = null;
        using TextFile text = CreateSavedText("edited shader");
        string yaml = Serialize(text);
        Assert.That(yaml, Does.Contain("Text: edited shader"));
        Assert.That(yaml, Does.Not.Contain("Path: original.asset"));
        Assert.That(_services.Probes, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void UnsupportedProbeStillRejectsReplacedDiscovery(bool installedSource)
    {
        if (installedSource) RuntimeAssetReadServices.SetSource(new RawSource());
        _files.OnProbe = () =>
        {
            RuntimeFileDiscoveryServices.Current = new Files();
            throw new NotSupportedException("host read became unavailable");
        };
        using TextFile text = CreateSavedText("edited shader");
        AssertStale(text);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void GenericConverterRetainsInlineSavedTextWhenBackingIsChangedOrUnreadable(bool unreadable)
    {
        if (unreadable) _files.OnRead = () => throw new IOException("unreadable backing file");
        using TextFile text = CreateSavedText("edited shader");

        string yaml = Serialize(text);

        Assert.That(yaml, Does.Contain("Text: edited shader"));
        Assert.That(yaml, Does.Not.Contain("Path: original.asset"));
        Assert.That(_services.Probes, Is.Zero);
    }

    [Test]
    public void ConfirmedMissingBackingFileStillAllowsSavedAssetReference()
    {
        _files.Exists = false;
        using TextFile text = CreateSavedText("saved shader");
        Assert.That(Serialize(text), Does.Contain("Path: original.asset"));
        Assert.That(_services.Probes, Is.EqualTo(1));
        Assert.That(_files.Reads, Is.Zero);
    }

    [Test]
    public void InlineScopeDoesNotSuppressLaterAssetReferences()
    {
        using TextFile inline = CreateSavedText("edited shader");
        Assert.That(Serialize(inline), Does.Contain("Text: edited shader"));
        using TextFile reference = CreateText("saved shader");
        reference.FilePath = Path.ChangeExtension(_path, ".asset");
        Assert.That(Serialize(reference), Does.Contain("Path: original.asset"));
    }

    [Test]
    public void MissingDiscoveryUsesOneCapturedRawSource()
    {
        RuntimeFileDiscoveryServices.Current = null;
        var raw = new RawSource { Bytes = Encoding.UTF8.GetBytes("shader source") };
        RuntimeAssetReadServices.SetSource(raw);
        Assert.That(Serialize(CreateText("shader source")), Does.Contain("Source: source.slang"));
        Assert.That(raw.Probes, Is.EqualTo(1));
        Assert.That(raw.Reads, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SourceReplacementDuringReadRejectsStaleResultEvenOnReadFailure(bool failRead)
    {
        _files.OnRead = () =>
        {
            RuntimeAssetReadServices.SetSource(null);
            if (failRead) throw new IOException("source changed while failing");
        };
        AssertStale(CreateText("shader source"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DiscoveryReplacementOrReinstallationDuringReadRejectsStaleResult(bool reinstall)
    {
        _files.OnRead = () => RuntimeFileDiscoveryServices.Current = reinstall ? _files : new Files();
        AssertStale(CreateText("shader source"));
    }

    [Test]
    public void DiscoveryReplacementDuringProbeRejectsResultBeforeRead()
    {
        _files.OnProbe = () => RuntimeFileDiscoveryServices.Current = new Files();
        AssertStale(CreateText("shader source"));
        Assert.That(_files.Reads, Is.Zero);
    }

    [Test]
    public void TextPathUsesCapturedSerializationOwnerAfterReadCallback()
    {
        _files.OnRead = () => _replacementInstallation = AssetSerializationServices.Install(new Services());
        Assert.That(Serialize(CreateText("shader source")), Does.Contain("Source: source.slang"));
    }

    [Test]
    public void AssetReferenceUsesSameOwnerForMembershipAndPortableReference()
    {
        var replacement = new Services { Reference = "replacement.asset" };
        _services.OnExists = () => _replacementInstallation = AssetSerializationServices.Install(replacement);
        _services.Reference = "original.asset";
        using TextFile text = CreateText("saved asset body");
        text.FilePath = Path.ChangeExtension(_path, ".asset");

        string yaml = Serialize(text);

        Assert.That(yaml, Does.Contain("Path: original.asset"));
        Assert.That(yaml, Does.Not.Contain("Text:"));
        Assert.That(_services.Probes, Is.EqualTo(1));
        Assert.That(_services.References, Is.EqualTo(1));
        Assert.That(replacement.Probes + replacement.References, Is.Zero);
        Assert.That(_files.Probes + _files.Reads, Is.Zero);
    }

    [Test]
    public void CallerThreadAssetReferenceUsesSerializationOwnerWithoutHostReads()
    {
        RuntimeAssetReadServices.IsCallerThread = true;
        RuntimeAssetReadServices.SetSource(new RawSource { HostAccess = false });
        using TextFile text = CreateText("asset body");
        text.FilePath = Path.ChangeExtension(_path, ".asset");
        Assert.That(Serialize(text), Does.Contain("Path: original.asset"));
        Assert.That(_services.Probes, Is.EqualTo(1));
        Assert.That(_files.Probes + _files.Reads, Is.Zero);
    }

    [Test]
    public void MissingAssetReferenceStaysInline()
    {
        _services.Exists = false;
        using TextFile text = CreateText("unsaved asset body");
        text.FilePath = Path.ChangeExtension(_path, ".asset");
        Assert.That(Serialize(text), Does.Contain("Text: unsaved asset body"));
        Assert.That(_services.References, Is.Zero);
    }

    private TextFile CreateText(string text) => new() { Text = text, FilePath = _path };

    private TextFile CreateSavedText(string text)
        => new() { Text = text, OriginalPath = _path, FilePath = Path.ChangeExtension(_path, ".asset") };

    private static string Serialize(TextFile text)
        => new SerializerBuilder()
            .WithTypeConverter(new XRAssetYamlConverter())
            .WithTypeConverter(new TextFileYamlTypeConverter())
            .WithEventEmitter(inner => new DepthTrackingEventEmitter(inner))
            .Build().Serialize(new Container { Source = text });

    private static void AssertStale(TextFile text)
    {
        using (text)
        {
            Exception error = Assert.Catch(() => new TextFileYamlTypeConverter().WriteYaml(
                new DiscardEmitter(), text, typeof(TextFile), (value, type) => { }))!;
            while (error.InnerException is not null) error = error.InnerException;
            Assert.That(error, Is.InstanceOf<OperationCanceledException>());
        }
    }

    private sealed class DiscardEmitter : IEmitter
    {
        public void Emit(ParsingEvent @event) { }
    }

    public sealed class Container { public TextFile? Source { get; set; } }

    private sealed class Files : IRuntimeFileDiscovery, IRuntimeHostFileReadBackend
    {
        public bool Exists = true;
        public byte[] Bytes = [];
        public int Probes;
        public int Reads;
        public Action? OnProbe;
        public Action? OnRead;
        public bool FileExists(string path) { Probes++; OnProbe?.Invoke(); return Exists; }
        public byte[] ReadAllBytes(string path) { Reads++; OnRead?.Invoke(); return Bytes; }
        public Stream OpenRead(string path, FileShare share) => throw new NotSupportedException();
        public IEnumerable<string> ReadLines(string path) => throw new NotSupportedException();
        public IEnumerable<string> EnumerateFiles(string path, string pattern, SearchOption option) => throw new NotSupportedException();
        public IEnumerable<string> EnumerateDirectories(string path, string pattern, SearchOption option) => throw new NotSupportedException();
        public IEnumerable<string> EnumerateFileSystemEntries(string path) => throw new NotSupportedException();
    }

    private sealed class RawSource : IRuntimeAssetReadSource
    {
        public bool HostAccess = true;
        public bool ThrowOnRead;
        public byte[] Bytes = [];
        public int Probes;
        public int Reads;
        public bool SupportsSynchronousReads => true;
        public bool SupportsHostFileAccess => HostAccess;
        public bool Exists(string path) { Probes++; return true; }
        public byte[] ReadAllBytes(string path)
        {
            Reads++;
            if (ThrowOnRead) throw new InvalidOperationException("Raw bytes must not replace host bytes.");
            return Bytes;
        }
        public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken token = default) => throw new NotSupportedException();
    }

    private sealed class Services : IAssetSerializationServices
    {
        public string AssetExtension => "asset";
        public string? GameAssetsPath => null;
        public string? EngineAssetsPath { get; init; }
        public string? CurrentDeserializationPath => null;
        public IReadOnlyList<IYamlTypeConverter> YamlTypeConverters => [];
        public bool Exists = true;
        public string Reference = "original.asset";
        public int Probes;
        public int References;
        public Action? OnExists;
        public bool AssetPathExists(string path) { Probes++; OnExists?.Invoke(); return Exists; }
        public bool TryCreatePortableAssetReference(string path, [NotNullWhen(true)] out string? reference)
        { References++; reference = Reference; return true; }
        public void EnsureYamlAssetRuntimeSupported(string? path = null) { }
        public bool TryGetAssetById(Guid id, [NotNullWhen(true)] out XRAsset? asset) { asset = null; return false; }
        public bool TryResolveAssetPathById(Guid id, string? reference, [NotNullWhen(true)] out string? path) { path = null; return false; }
        public XRAsset? LoadImmediate(string path, Type type) => throw new NotSupportedException();
        public bool TryDeferAssetLoad(string path, Type type, out XRAsset? asset) { asset = null; return false; }
        public bool TryHandleScalarAsset(IParser parser, Type type, Scalar scalar, out object? value) { value = null; return false; }
    }
}
