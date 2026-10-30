using System.Text;
using NUnit.Framework;
using XREngine;
using XREngine.Audio;
using XREngine.Components;
using XREngine.Data;

namespace XREngine.UnitTests.Audio;

[TestFixture, NonParallelizable]
public sealed class Audio2Face3DCsvHostReadTests
{
    private const string Csv = "timecode,JawOpen\n0,0.25\n";
    private IRuntimeFileDiscovery? _previousDiscovery;
    private IRuntimeAssetReadSource? _previousSource;
    private bool _previousCaller;
    private IRuntimeAudioIntegrationServices _previousAudio = null!;

    [SetUp]
    public void SetUp()
    {
        _previousDiscovery = RuntimeFileDiscoveryServices.Current;
        _previousSource = RuntimeAssetReadServices.Source;
        _previousCaller = RuntimeAssetReadServices.IsCallerThread;
        _previousAudio = RuntimeAudioIntegrationServices.Current;
        RuntimeAssetReadServices.SetSource(null);
        RuntimeAssetReadServices.IsCallerThread = false;
    }

    [TearDown]
    public void TearDown()
    {
        RuntimeAssetReadServices.SetSource(_previousSource);
        RuntimeAssetReadServices.IsCallerThread = _previousCaller;
        RuntimeFileDiscoveryServices.Current = _previousDiscovery;
        RuntimeAudioIntegrationServices.Current = _previousAudio;
    }

    [Test]
    public void ReloadReadsCapturedProviderAndDetectsUtf8Bom()
    {
        var files = new CsvFiles([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Csv)]);
        RuntimeFileDiscoveryServices.Current = files;
        var component = new Audio2Face3DComponent { ReloadOnActivation = false, AnimationCsvPath = "virtual.csv" };

        Assert.That(component.ReloadAnimation(), Is.True, component.LastLoadError);
        Assert.That(files.Probes, Is.EqualTo(1));
        Assert.That(files.Reads, Is.EqualTo(1));
        Assert.That(files.LastPath, Is.EqualTo(Path.GetFullPath("virtual.csv")));
        Assert.That(component.LastLoadError, Is.Empty);
    }

    [Test]
    public void MissingReadCapabilityReportsError()
    {
        RuntimeFileDiscoveryServices.Current = new EnumerationOnly();
        var component = new Audio2Face3DComponent { ReloadOnActivation = false, AnimationCsvPath = "virtual.csv" };

        Assert.That(component.ReloadAnimation(), Is.False);
        Assert.That(component.LastLoadError, Does.Contain("AssetSource.HostFileReadUnavailable"));
    }

    [Test]
    public void ProviderReplacementDuringReadRejectsPublication()
    {
        var files = new CsvFiles(Encoding.UTF8.GetBytes(Csv));
        RuntimeFileDiscoveryServices.Current = files;
        var component = new Audio2Face3DComponent { ReloadOnActivation = false, AnimationCsvPath = "virtual.csv" };
        Assert.That(component.ReloadAnimation(), Is.True);
        files.OnRead = () => RuntimeFileDiscoveryServices.Current = new CsvFiles(Encoding.UTF8.GetBytes(Csv));

        Assert.That(component.ReloadAnimation(), Is.False);
        Assert.That(component.LastLoadError, Does.Contain("StaleFileDiscovery"));
        Assert.That(component.PlayFromStart(), Is.True);
    }

    [Test]
    public void PathChangeDuringReadRejectsOldAnimation()
    {
        var files = new CsvFiles(Encoding.UTF8.GetBytes(Csv));
        RuntimeFileDiscoveryServices.Current = files;
        var component = new Audio2Face3DComponent { ReloadOnActivation = false, AnimationCsvPath = "old.csv" };
        files.OnRead = () => component.AnimationCsvPath = "new.csv";

        Assert.That(component.ReloadAnimation(), Is.False);
        Assert.That(component.LastLoadError, Does.Contain("Audio2Face3D.StaleCsvRead"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AudioHostReplacementDuringReadRejectsPublication(bool reinstallSameInstance)
    {
        var host = new AudioHost();
        RuntimeAudioIntegrationServices.Current = host;
        var files = new CsvFiles(Encoding.UTF8.GetBytes(Csv));
        RuntimeFileDiscoveryServices.Current = files;
        var component = new Audio2Face3DComponent { ReloadOnActivation = false, AnimationCsvPath = "virtual.csv" };
        files.OnRead = () => RuntimeAudioIntegrationServices.Current = reinstallSameInstance ? host : new AudioHost();

        Assert.That(component.ReloadAnimation(), Is.False);
        Assert.That(component.LastLoadError, Does.Contain("Audio2Face3D.StaleCsvRead"));
    }

    [Test]
    public void AudioHostProjectDirectoryChangeDuringReadRejectsPublication()
    {
        var host = new AudioHost { ProjectDirectory = Path.GetTempPath() };
        RuntimeAudioIntegrationServices.Current = host;
        var files = new CsvFiles(Encoding.UTF8.GetBytes(Csv));
        RuntimeFileDiscoveryServices.Current = files;
        var component = new Audio2Face3DComponent { ReloadOnActivation = false, AnimationCsvPath = "virtual.csv" };
        files.OnRead = () => host.ProjectDirectory = Path.GetPathRoot(Path.GetTempPath());

        Assert.That(component.ReloadAnimation(), Is.False);
        Assert.That(component.LastLoadError, Does.Contain("Audio2Face3D.StaleCsvRead"));
    }

    [Test]
    public void NewerReloadRejectsOlderRead()
    {
        var files = new CsvFiles(Encoding.UTF8.GetBytes(Csv));
        RuntimeFileDiscoveryServices.Current = files;
        var component = new Audio2Face3DComponent { ReloadOnActivation = false, AnimationCsvPath = "virtual.csv" };
        files.OnRead = () =>
        {
            files.OnRead = null;
            files.Bytes = Encoding.UTF8.GetBytes("timecode,JawOpen\n0,0.25\n1,0.75\n");
            Assert.That(component.ReloadAnimation(), Is.True);
        };

        Assert.That(component.ReloadAnimation(), Is.False);
        Assert.That(component.LastLoadError, Is.Empty);
        Assert.That(component.Duration, Is.EqualTo(1.0f));
        Assert.That(component.PlayFromStart(), Is.True);
    }

    [Test]
    public void PropertyNotificationCannotPublishDuringPathChange()
    {
        RuntimeFileDiscoveryServices.Current = new CsvFiles(Encoding.UTF8.GetBytes(Csv));
        var component = new Audio2Face3DComponent { ReloadOnActivation = false, AnimationCsvPath = "old.csv" };
        bool? nestedResult = null;
        component.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(Audio2Face3DComponent.AnimationCsvPath))
                nestedResult = component.ReloadAnimation();
        };

        component.AnimationCsvPath = "new.csv";

        Assert.That(nestedResult, Is.False);
        Assert.That(component.LastLoadError, Does.Contain("Audio2Face3D.StaleCsvRead"));
    }

    [Test]
    public void OlderMissingProbeCannotOverwriteNewerSuccess()
    {
        var files = new CsvFiles(Encoding.UTF8.GetBytes(Csv));
        RuntimeFileDiscoveryServices.Current = files;
        var component = new Audio2Face3DComponent { ReloadOnActivation = false, AnimationCsvPath = "virtual.csv" };
        files.OnProbe = () =>
        {
            files.OnProbe = null;
            files.Exists = true;
            Assert.That(component.ReloadAnimation(), Is.True);
        };
        files.Exists = false;

        Assert.That(component.ReloadAnimation(), Is.False);
        Assert.That(component.LastLoadError, Is.Empty);
        Assert.That(component.PlayFromStart(), Is.True);
    }

    private class EnumerationOnly : IRuntimeFileDiscovery
    {
        public IEnumerable<string> EnumerateFiles(string path, string pattern, SearchOption option) => [];
        public IEnumerable<string> EnumerateDirectories(string path, string pattern, SearchOption option) => [];
        public IEnumerable<string> EnumerateFileSystemEntries(string path) => [];
    }

    private sealed class CsvFiles(byte[] bytes) : EnumerationOnly, IRuntimeHostFileReadBackend
    {
        public byte[] Bytes = bytes;
        public int Probes;
        public int Reads;
        public string? LastPath;
        public Action? OnRead;
        public Action? OnProbe;
        public bool Exists = true;

        public bool FileExists(string path)
        {
            Probes++;
            LastPath = path;
            bool exists = Exists;
            OnProbe?.Invoke();
            return exists;
        }
        public Stream OpenRead(string path, FileShare share)
        {
            Reads++;
            LastPath = path;
            byte[] content = Bytes;
            OnRead?.Invoke();
            return new MemoryStream(content, writable: false);
        }
        public byte[] ReadAllBytes(string path) => throw new NotSupportedException();
        public IEnumerable<string> ReadLines(string path) => throw new NotSupportedException();
    }

    private sealed class AudioHost : IRuntimeAudioIntegrationServices
    {
        public int SampleRate => 48000;
        public long ElapsedTicks => 0;
        public float UpdateDeltaSeconds => 0;
        public string? ProjectDirectory { get; set; }
        public ListenerContext NewListener(string? name) => throw new NotSupportedException();
        public AudioData? LoadAudioData(string path) => throw new NotSupportedException();
    }
}
