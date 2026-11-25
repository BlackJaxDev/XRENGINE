using System.Reflection;
using XREngine.Core.Files;
using XREngine.Runtime.Platform.Desktop;

namespace XREngine.UnitTests.Core;

/// <summary>Installs isolated authoring roots and restores the shared test host on disposal.</summary>
internal sealed class NativeAssetRecoveryTestScope : IDisposable
{
    private readonly bool _usesEngine;
    private readonly IAssetFileSystem? _previousFileSystem;
    private readonly string? _previousAssets, _previousMetadata, _previousCache, _previousProject;
    private readonly bool _previousMonitor;
    private readonly Dictionary<string, string?> _environment = new();

    public NativeAssetRecoveryTestScope(bool useEngineManager = false)
    {
        ProjectRoot = Path.Combine(Path.GetTempPath(), "xr-native-recovery-" + Guid.NewGuid().ToString("N"));
        AssetsRoot = Path.Combine(ProjectRoot, "Assets");
        MetadataRoot = Path.Combine(ProjectRoot, "Metadata");
        CacheRoot = Path.Combine(ProjectRoot, "Cache");
        foreach (string leaf in new[] { AssetsRoot, MetadataRoot, CacheRoot, Path.Combine(ProjectRoot, "EngineAssets") })
            Directory.CreateDirectory(leaf);
        SetEnvironment("XRE_GAME_ASSETS_PATH", AssetsRoot);
        SetEnvironment("XRE_GAME_METADATA_PATH", MetadataRoot);
        SetEnvironment("XRE_GAME_CACHE_PATH", CacheRoot);
        SetEnvironment("XRE_ENGINE_ASSETS_PATH", Path.Combine(ProjectRoot, "EngineAssets"));
        _previousFileSystem = AssetFileSystemServices.Current;
        AssetFileSystemServices.Current = new DesktopAssetFileSystem();
        _usesEngine = useEngineManager;
        Manager = useEngineManager ? Engine.Assets : new AssetManager();
        _previousAssets = Manager.GameAssetsPath;
        _previousMetadata = Manager.GameMetadataPath;
        _previousCache = Manager.GameCachePath;
        _previousProject = (string?)typeof(AssetManager).GetField("_authoringProjectRoot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Manager);
        _previousMonitor = Manager.MonitorGameAssetsForChanges;
        Manager.MonitorGameAssetsForChanges = false;
        Manager.ConfigureAuthoringRoots(AssetsRoot, MetadataRoot, CacheRoot, ProjectRoot);
    }

    public AssetManager Manager { get; }
    public string ProjectRoot { get; }
    public string AssetsRoot { get; }
    public string MetadataRoot { get; }
    public string CacheRoot { get; }

    public void Reopen() => Manager.ConfigureAuthoringRoots(AssetsRoot, MetadataRoot, CacheRoot, ProjectRoot);

    private void SetEnvironment(string name, string value)
    {
        _environment.Add(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        try
        {
            if (!_usesEngine) Manager.Dispose();
            AssetFileSystemServices.Current = _previousFileSystem;
            if (_usesEngine)
            {
                Manager.ConfigureAuthoringRoots(_previousAssets, _previousMetadata, _previousCache, _previousProject);
                Manager.MonitorGameAssetsForChanges = _previousMonitor;
            }
        }
        finally
        {
            foreach ((string name, string? value) in _environment)
                Environment.SetEnvironmentVariable(name, value);
            // Leave exact retained bytes available when a test fails. The outer test-temp scope owns cleanup.
        }
    }
}
