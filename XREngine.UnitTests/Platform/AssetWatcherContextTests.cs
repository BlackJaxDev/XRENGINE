using NUnit.Framework;
using Shouldly;
using XREngine.Core.Engine;
using XREngine.Core.Files;
using XREngine.Runtime.Platform.Desktop;

namespace XREngine.UnitTests.Platform;

[TestFixture, NonParallelizable]
public sealed class AssetWatcherContextTests
{
    [Test]
    public void OldInstallationEventCannotEnterSamePathProject()
    {
        using var fixture = new WatcherFixture();
        string asset = Path.Combine(fixture.AssetsRoot, "item.asset");
        File.WriteAllText(asset, "ID: " + Guid.NewGuid());
        ManualMonitor old = fixture.CurrentMonitor;

        fixture.Manager.InvalidateAuthoringContext();
        old.RaiseCreated(asset);
        File.Exists(fixture.MetadataPath(asset)).ShouldBeFalse();

        fixture.CurrentMonitor.RaiseCreated(asset);
        File.Exists(fixture.MetadataPath(asset)).ShouldBeTrue();
    }

    [Test]
    public void BackendRoundTripRejectsOldMonitorGeneration()
    {
        using var fixture = new WatcherFixture();
        string asset = Path.Combine(fixture.AssetsRoot, "item.asset");
        File.WriteAllText(asset, "ID: " + Guid.NewGuid());
        ManualMonitor old = fixture.CurrentMonitor;

        AssetFileSystemServices.Current = new RecordingBackend();
        old.RaiseCreated(asset);
        AssetFileSystemServices.Current = fixture.Backend;
        old.RaiseCreated(asset);

        File.Exists(fixture.MetadataPath(asset)).ShouldBeFalse();
        fixture.CurrentMonitor.RaiseCreated(asset);
        File.Exists(fixture.MetadataPath(asset)).ShouldBeTrue();
    }

    [Test]
    public void BackendSwapDuringMonitorCreationDoesNotPairWrongBackend()
    {
        using var fixture = new WatcherFixture();
        string asset = Path.Combine(fixture.AssetsRoot, "item.asset");
        File.WriteAllText(asset, "ID: " + Guid.NewGuid());
        ManualMonitor old = fixture.CurrentMonitor;
        RecordingBackend replacement = new();
        fixture.Backend.OnCreateMonitor = () => AssetFileSystemServices.Current = replacement;

        fixture.Manager.InvalidateAuthoringContext();
        old.RaiseCreated(asset);
        File.Exists(fixture.MetadataPath(asset)).ShouldBeFalse();
        fixture.Manager.GameWatcher.CurrentInstallation.ShouldBeNull();

        fixture.Manager.InvalidateAuthoringContext();
        fixture.CurrentMonitor.RaiseCreated(asset);
        File.Exists(fixture.MetadataPath(asset)).ShouldBeTrue();
    }

    [Test]
    public void SynchronousEnableCallbackIsRejectedDuringRebind()
    {
        using var fixture = new WatcherFixture();
        string asset = Path.Combine(fixture.AssetsRoot, "item.asset");
        File.WriteAllText(asset, "ID: " + Guid.NewGuid());
        fixture.Backend.OnEnableMonitor = monitor => monitor.RaiseCreated(asset);

        fixture.Manager.InvalidateAuthoringContext();

        File.Exists(fixture.MetadataPath(asset)).ShouldBeFalse();
        fixture.CurrentMonitor.RaiseCreated(asset);
        File.Exists(fixture.MetadataPath(asset)).ShouldBeTrue();
    }

    [Test]
    public void NativeDisposeWaitDoesNotBlockTerminalRebindCheck()
    {
        using var fixture = new WatcherFixture();
        using var monitor = new AssetChangeMonitor { Path = fixture.AssetsRoot };
        monitor.EnableRaisingEvents = true;
        ManualMonitor native = (ManualMonitor)monitor.CurrentInstallation!.Monitor;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        native.OnDispose = () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
        };

        Task disposal = Task.Run(monitor.Dispose);
        try
        {
            entered.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
            Task rebind = Task.Run(monitor.Rebind);
            rebind.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
            Should.Throw<ObjectDisposedException>(() => monitor.EnableRaisingEvents = true);
            monitor.CurrentInstallation.ShouldBeNull();
        }
        finally
        {
            release.Set();
            disposal.Wait(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public void DisposeDuringProviderCreationCannotPublishMonitor()
    {
        using var fixture = new WatcherFixture();
        using var monitor = new AssetChangeMonitor { Path = fixture.AssetsRoot };
        fixture.Backend.OnCreateMonitor = monitor.Dispose;

        monitor.EnableRaisingEvents = true;

        monitor.CurrentInstallation.ShouldBeNull();
        Should.Throw<ObjectDisposedException>(() => monitor.EnableRaisingEvents = true);
    }

    [Test]
    public void DisableDuringProviderCreationCancelsOnlyThatInstallation()
    {
        using var fixture = new WatcherFixture();
        using var monitor = new AssetChangeMonitor { Path = fixture.AssetsRoot };
        fixture.Backend.OnCreateMonitor = () => monitor.EnableRaisingEvents = false;

        monitor.EnableRaisingEvents = true;

        monitor.CurrentInstallation.ShouldBeNull();
        fixture.Backend.OnCreateMonitor = null;
        monitor.EnableRaisingEvents = true;
        monitor.CurrentInstallation.ShouldNotBeNull();
    }

    [Test]
    public void RestoredFileRejectsQueuedDeleteAndAbsentFileRejectsCreate()
    {
        using var fixture = new WatcherFixture();
        string asset = Path.Combine(fixture.AssetsRoot, "item.asset");
        string absent = Path.Combine(fixture.AssetsRoot, "absent.asset");
        File.WriteAllText(asset, "ID: " + Guid.NewGuid());
        fixture.Manager.EnsureMetadataForAssetPath(asset, false);
        string metadata = fixture.MetadataPath(asset);
        string original = File.ReadAllText(metadata);

        ManualMonitor monitor = fixture.CurrentMonitor;
        monitor.RaiseDeleted(asset);
        monitor.RaiseCreated(absent);
        monitor.RaiseChanged(absent);
        File.ReadAllText(metadata).ShouldBe(original);
        File.Exists(fixture.MetadataPath(absent)).ShouldBeFalse();
    }

    [Test]
    public void RenameDoesNotReplaceOccupiedDestinationMetadata()
    {
        using var fixture = new WatcherFixture();
        string source = Path.Combine(fixture.AssetsRoot, "old.asset");
        string destination = Path.Combine(fixture.AssetsRoot, "new.asset");
        File.WriteAllText(source, "ID: " + Guid.NewGuid());
        fixture.Manager.EnsureMetadataForAssetPath(source, false);
        string sourceMetadata = fixture.MetadataPath(source);
        string destinationMetadata = fixture.MetadataPath(destination);
        File.WriteAllText(destinationMetadata, "occupied and malformed");
        File.Move(source, destination);

        fixture.CurrentMonitor.RaiseRenamed(source, destination);

        File.ReadAllText(destinationMetadata).ShouldBe("occupied and malformed");
        File.Exists(sourceMetadata).ShouldBeTrue();
    }

    private sealed class WatcherFixture : IDisposable
    {
        private readonly IAssetFileSystem? _previousBackend;
        private readonly string? _previousAssetsEnv;
        public string Root { get; }
        public string AssetsRoot { get; }
        public string MetadataRoot { get; }
        public RecordingBackend Backend { get; } = new();
        public AssetManager Manager { get; }
        public ManualMonitor CurrentMonitor => (ManualMonitor)Manager.GameWatcher.CurrentInstallation!.Monitor;

        public WatcherFixture()
        {
            Root = Path.GetFullPath(Path.Combine("Build", "_AgentValidation", "00000000-000000-shared",
                "scratch", "asset-watcher-" + Guid.NewGuid().ToString("N")));
            AssetsRoot = Path.Combine(Root, "Assets");
            MetadataRoot = Path.Combine(Root, "Metadata");
            string engineRoot = Path.Combine(Root, "EngineAssets");
            Directory.CreateDirectory(AssetsRoot);
            Directory.CreateDirectory(MetadataRoot);
            Directory.CreateDirectory(engineRoot);
            _previousBackend = AssetFileSystemServices.Current;
            _previousAssetsEnv = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.GameAssetsPath);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.GameAssetsPath, AssetsRoot);
            AssetFileSystemServices.Current = Backend;
            Manager = new AssetManager(engineRoot);
            Manager.GameMetadataPath = MetadataRoot;
        }

        public string MetadataPath(string asset)
            => Path.Combine(MetadataRoot, Path.GetRelativePath(AssetsRoot, asset)) + ".meta";

        public void Dispose()
        {
            Manager.Dispose();
            AssetFileSystemServices.Current = _previousBackend;
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.GameAssetsPath, _previousAssetsEnv);
            if (Directory.Exists(Root))
                Directory.Delete(Root, true);
        }
    }

    private sealed class RecordingBackend : IAssetFileSystem, IAssetMetadataFileBackend
    {
        private readonly DesktopAssetFileSystem _files = new();
        public List<ManualMonitor> Monitors { get; } = [];
        public Action? OnCreateMonitor { get; set; }
        public Action<ManualMonitor>? OnEnableMonitor { get; set; }
        public bool SupportsChangeNotifications => true;
        public IAssetChangeMonitor CreateChangeMonitor()
        {
            ManualMonitor monitor = new();
            monitor.OnEnable = () => OnEnableMonitor?.Invoke(monitor);
            Monitors.Add(monitor);
            OnCreateMonitor?.Invoke();
            return monitor;
        }
        public IEnumerable<string> EnumerateFiles(string path, string pattern, SearchOption option)
            => _files.EnumerateFiles(path, pattern, option);
        public IEnumerable<string> EnumerateDirectories(string path, string pattern, SearchOption option)
            => _files.EnumerateDirectories(path, pattern, option);
        public IEnumerable<string> EnumerateFileSystemEntries(string path)
            => _files.EnumerateFileSystemEntries(path);
        public bool FileExists(string path) => _files.FileExists(path);
        public bool DirectoryExists(string path) => _files.DirectoryExists(path);
        public void CreateDirectory(string path) => _files.CreateDirectory(path);
        public void DeleteDirectory(string path) => _files.DeleteDirectory(path);
        public void DeleteFile(string path) => _files.DeleteFile(path);
        public void MoveFile(string source, string destination, bool overwrite) => _files.MoveFile(source, destination, overwrite);
        public void CopyFile(string source, string destination, bool overwrite) => _files.CopyFile(source, destination, overwrite);
        public string ReadAllText(string path) => _files.ReadAllText(path);
        public void WriteAllText(string path, string contents) => _files.WriteAllText(path, contents);
        public Guid TryExtractAssetGuid(string path) => _files.TryExtractAssetGuid(path);
        public DateTime GetLastWriteTimeUtc(string path) => _files.GetLastWriteTimeUtc(path);
    }

    private sealed class ManualMonitor : IAssetChangeMonitor
    {
        private bool _enabled;
        public Action? OnEnable { get; set; }
        public Action? OnDispose { get; set; }
        public string Path { get; set; } = string.Empty;
        public string Filter { get; set; } = "*.*";
        public bool IncludeSubdirectories { get; set; }
        public bool EnableRaisingEvents
        {
            get => _enabled;
            set
            {
                _enabled = value;
                if (value)
                    OnEnable?.Invoke();
            }
        }
        public AssetNotifyFilters NotifyFilter { get; set; }
        public event Action<object, AssetFileChangeEventArgs>? Created;
        public event Action<object, AssetFileChangeEventArgs>? Changed;
        public event Action<object, AssetFileChangeEventArgs>? Deleted;
        public event Action<object, AssetFileRenameEventArgs>? Renamed;
        public event Action<object, AssetMonitorErrorEventArgs>? Error { add { } remove { } }
        public void RaiseCreated(string path) => Created?.Invoke(this,
            new AssetFileChangeEventArgs(AssetFileChangeKind.Created, path, System.IO.Path.GetFileName(path)));
        public void RaiseDeleted(string path) => Deleted?.Invoke(this,
            new AssetFileChangeEventArgs(AssetFileChangeKind.Deleted, path, System.IO.Path.GetFileName(path)));
        public void RaiseChanged(string path) => Changed?.Invoke(this,
            new AssetFileChangeEventArgs(AssetFileChangeKind.Changed, path, System.IO.Path.GetFileName(path)));
        public void RaiseRenamed(string oldPath, string newPath) => Renamed?.Invoke(this,
            new AssetFileRenameEventArgs(newPath, System.IO.Path.GetFileName(newPath), oldPath, System.IO.Path.GetFileName(oldPath)));
        public void Dispose() => OnDispose?.Invoke();
    }
}
