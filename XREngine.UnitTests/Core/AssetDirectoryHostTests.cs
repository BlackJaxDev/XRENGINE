using NUnit.Framework;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Execution;

namespace XREngine.UnitTests.Core;

[TestFixture, NonParallelizable]
public sealed class AssetDirectoryHostTests
{
    private static readonly string[] Variables =
    [
        XREngineEnvironmentVariables.EngineAssetsPath,
        XREngineEnvironmentVariables.GameAssetsPath,
        XREngineEnvironmentVariables.GameCachePath,
        XREngineEnvironmentVariables.GameMetadataPath,
    ];
    private string?[] _previousVariables = null!;
    private IAssetFileSystem? _previousFiles;
    private IRuntimeFileDiscovery? _previousDiscovery;
    private IRuntimeAssetReadSource? _previousSource;
    private bool _previousCaller;
    private AssetDirectoryTestFileSystem _files = null!;
    private string _root = null!;
    private AssetManager? _manager;
    private JobManager? _jobs;

    [SetUp]
    public void SetUp()
    {
        _previousVariables = Variables.Select(Environment.GetEnvironmentVariable).ToArray();
        _previousFiles = AssetFileSystemServices.Current;
        _previousDiscovery = RuntimeFileDiscoveryServices.Current;
        _previousSource = RuntimeAssetReadServices.Source;
        _previousCaller = RuntimeAssetReadServices.IsCallerThread;
        RuntimeAssetReadServices.SetSource(null);
        RuntimeAssetReadServices.IsCallerThread = false;
        _files = new AssetDirectoryTestFileSystem();
        AssetFileSystemServices.Current = _files;
        _root = Path.GetFullPath(Path.Combine("Build", "_AgentValidation", "00000000-000000-shared", "scratch", "virtual-assets-" + Guid.NewGuid().ToString("N")));
        _files.Directories.Add(_root);
        _files.Directories.Add(Path.Combine(_root, "Engine"));
        foreach (string variable in Variables)
            Environment.SetEnvironmentVariable(variable, null);
        Environment.SetEnvironmentVariable(Variables[1], Path.Combine(_root, "Assets"));
    }

    [TearDown]
    public void TearDown()
    {
        _manager?.Dispose();
        _manager = null;
        if (_jobs is not null)
        {
            Assert.That(_jobs.Shutdown(waitForWorkers: true), Is.True);
            _jobs = null;
        }
        for (int i = 0; i < Variables.Length; i++)
            Environment.SetEnvironmentVariable(Variables[i], _previousVariables[i]);
        RuntimeAssetReadServices.SetSource(_previousSource);
        RuntimeAssetReadServices.IsCallerThread = _previousCaller;
        AssetFileSystemServices.Current = _previousFiles;
        RuntimeFileDiscoveryServices.Current = _previousDiscovery;
    }

    [Test]
    public async Task NativeAssetJobsKeepNestedWorkerWorkInline()
    {
        ThreadedWorkerBackend.EnsureRegistered();
        JobManager jobs = _jobs = new(workerCount: 1);
        AssetManager manager = _manager = new AssetManager(Path.Combine(_root, "Engine"), () => jobs);
        int result = await Task.Run(() => manager.RunAssetJobBlocking(() =>
        {
            int worker = Environment.CurrentManagedThreadId;
            return manager.RunAssetJobBlocking(() =>
            {
                Assert.That(Environment.CurrentManagedThreadId, Is.EqualTo(worker));
                return 42;
            });
        })).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(result, Is.EqualTo(42));
    }

    [Test]
    public void CallerAssetJobsRejectSchedulingButAllowExplicitInlineWork()
    {
        JobManager jobs = _jobs = new(executionMode: JobExecutionMode.CallerThread);
        AssetManager manager = _manager = new AssetManager(Path.Combine(_root, "Engine"), () => jobs);
        int calls = 0;
        int Work() => ++calls;
        Assert.Throws<NotSupportedException>(() => manager.RunAssetJobBlocking(Work));
        Assert.That(calls, Is.Zero);
        Assert.That(manager.RunAssetJobBlocking(Work, bypassJobThread: true), Is.EqualTo(1));
    }

    [Test]
    public void ConstructorUsesVirtualDirectoriesAndCreatesWritableRootsThroughProvider()
    {
        AssetManager manager = _manager = new AssetManager(Path.Combine(_root, "Engine"));
        Assert.That(manager.EngineAssetsPath, Is.EqualTo(Path.Combine(_root, "Engine")));
        Assert.That(manager.GameAssetsPath, Is.EqualTo(Path.Combine(_root, "Assets")));
        Assert.That(manager.GameMetadataPath, Is.EqualTo(Path.Combine(_root, "Metadata")));
        Assert.That(manager.GameCachePath, Is.EqualTo(Path.Combine(_root, "Cache")));
        Assert.That(_files.Created, Is.EquivalentTo(new[] { manager.GameAssetsPath, manager.GameMetadataPath, manager.GameCachePath }));
        Assert.That(Directory.Exists(_root), Is.False, "The provider owns all physical directory operations.");
    }

    [Test]
    public void ProjectRootInferenceUsesProviderAssetsChild()
    {
        string project = Path.Combine(_root, "Project");
        _files.Directories.Add(project);
        _files.Directories.Add(Path.Combine(project, "Assets"));
        Environment.SetEnvironmentVariable(Variables[1], project);
        AssetManager manager = _manager = new AssetManager(Path.Combine(_root, "Engine"));
        Assert.That(manager.GameMetadataPath, Is.EqualTo(Path.Combine(project, "Metadata")));
        Assert.That(manager.GameCachePath, Is.EqualTo(Path.Combine(project, "Cache")));
    }

    [Test]
    public void ConstructorDiscoversCommonAssetsAndWorkingDirectoryAssetsThroughProvider()
    {
        Environment.SetEnvironmentVariable(Variables[1], null);
        string engine = Path.Combine(AppContext.BaseDirectory, "Build", "CommonAssets");
        string game = Path.Combine(Environment.CurrentDirectory, "Assets");
        _files.Directories.Add(engine);
        _files.Directories.Add(Path.Combine(engine, "Shaders"));
        _files.Directories.Add(game);
        _files.Directories.Add(Environment.CurrentDirectory);
        AssetManager manager = _manager = new AssetManager();
        Assert.That(manager.EngineAssetsPath, Is.EqualTo(engine));
        Assert.That(manager.GameAssetsPath, Is.EqualTo(game));
        Assert.That(_files.Probes, Does.Contain(Path.Combine(engine, "Shaders")));
    }

    [Test]
    public void MissingEngineOverrideRetainsDirectoryNotFoundDiagnostic()
    {
        string missing = Path.Combine(_root, "MissingEngine");
        Environment.SetEnvironmentVariable(Variables[0], missing);
        DirectoryNotFoundException error = Assert.Throws<DirectoryNotFoundException>(() => new AssetManager())!;
        Assert.That(error.Message, Does.Contain(missing));
    }

    [Test]
    public void AssetPathPreparationKeepsOneProviderForRecursiveCreation()
    {
        string directory = Path.Combine(_root, "one", "two");
        var replacement = new AssetDirectoryTestFileSystem();
        _files.OnProbe = () => AssetFileSystemServices.Current = replacement;
        using var asset = new TextFile { Name = "sample" };
        Assert.That(AssetManager.VerifyAssetPath(asset, directory), Is.EqualTo(Path.Combine(directory, "sample.asset")));
        Assert.That(_files.Created, Is.EqualTo(new[] { Path.Combine(_root, "one"), directory }));
        Assert.That(replacement.Probes, Is.Empty);
        Assert.That(replacement.Created, Is.Empty);
    }

    [Test]
    public void CallerThreadRejectsDirectoryPreparationBeforeProbe()
    {
        RuntimeAssetReadServices.IsCallerThread = true;
        using var asset = new TextFile();
        Assert.Throws<NotSupportedException>(() => AssetManager.VerifyAssetPath(asset, Path.Combine(_root, "blocked")));
        Assert.That(_files.Probes, Is.Empty);
        Assert.That(_files.Created, Is.Empty);
    }

    [Test]
    public void MissingProviderReportsCapabilityBeforePhysicalDirectoryWork()
    {
        AssetFileSystemServices.Current = null;
        Assert.Throws<NotSupportedException>(() => new AssetManager(Path.Combine(_root, "Engine")));
        Assert.That(Directory.Exists(_root), Is.False);
    }
}
