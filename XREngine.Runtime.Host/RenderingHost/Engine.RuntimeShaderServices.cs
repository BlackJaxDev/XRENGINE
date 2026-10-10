using XREngine.Core.Files;
using XREngine.Rendering;

namespace XREngine;

internal sealed class EngineRuntimeShaderServices : IRuntimeShaderServices, IRuntimeShaderChangeSource, IDisposable
{
    private readonly AssetManager _assets = Engine.Assets;
    private int _disposed;

    /// <summary>Installs managed shader asset access without requiring a graphics backend.</summary>
    public static IDisposable Install()
    {
        IRuntimeShaderServices? previous = RuntimeShaderServices.Current;
        EngineRuntimeShaderServices installed = new();
        InstallationLease lease = new(installed, previous);
        try
        {
            RuntimeShaderServices.Current = installed;
            return lease;
        }
        catch (Exception installationFailure)
        {
            try
            {
                lease.Dispose();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(installationFailure, cleanupFailure);
            }
            throw;
        }
    }

    public EngineRuntimeShaderServices()
    {
        _assets.EngineFileCreated += OnFileCreated;
        _assets.EngineFileChanged += OnFileChanged;
        _assets.EngineFileDeleted += OnFileDeleted;
        _assets.EngineFileRenamed += OnFileRenamed;
        _assets.GameFileCreated += OnFileCreated;
        _assets.GameFileChanged += OnFileChanged;
        _assets.GameFileDeleted += OnFileDeleted;
        _assets.GameFileRenamed += OnFileRenamed;
    }

    public event Action<ShaderSourceFileChange>? ShaderSourceFileChanged;

    public bool SupportsSynchronousShaderWork => _assets.SupportsSynchronousAssetWork;

    public int ShaderAssetCacheVersion => _assets.RuntimeSourceEpoch;

    public XRShader? GetCachedEngineShader(string assetRoot, string relativePath)
        => _assets.TryGetCachedEngineAsset(assetRoot, relativePath, out XRShader? shader) ? shader : null;

    public T? LoadAsset<T>(string filePath) where T : XRAsset, new()
        => _assets.Load<T>(filePath);

    public T LoadEngineAsset<T>(JobPriority priority, bool bypassJobThread, string assetRoot, string relativePath) where T : XRAsset, new()
        => _assets.LoadEngineAsset<T>(priority, bypassJobThread, assetRoot, relativePath);

    public Task<T> LoadEngineAssetAsync<T>(JobPriority priority, bool bypassJobThread, string assetRoot, string relativePath) where T : XRAsset, new()
        => _assets.LoadEngineAssetAsync<T>(priority, bypassJobThread, assetRoot, relativePath);

    public void LogWarning(string message)
        => Debug.LogWarning(message);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _assets.EngineFileCreated -= OnFileCreated;
        _assets.EngineFileChanged -= OnFileChanged;
        _assets.EngineFileDeleted -= OnFileDeleted;
        _assets.EngineFileRenamed -= OnFileRenamed;
        _assets.GameFileCreated -= OnFileCreated;
        _assets.GameFileChanged -= OnFileChanged;
        _assets.GameFileDeleted -= OnFileDeleted;
        _assets.GameFileRenamed -= OnFileRenamed;
    }

    private sealed class InstallationLease(EngineRuntimeShaderServices installed, IRuntimeShaderServices? previous) : IDisposable
    {
        private EngineRuntimeShaderServices? _installed = installed;

        public void Dispose()
        {
            EngineRuntimeShaderServices? installed = Interlocked.Exchange(ref _installed, null);
            if (installed is null)
                return;
            try
            {
                if (ReferenceEquals(RuntimeShaderServices.Current, installed))
                    RuntimeShaderServices.Current = previous;
            }
            finally
            {
                installed.Dispose();
            }
        }
    }

    private void OnFileCreated(AssetFileChangeEventArgs args)
        => ShaderSourceFileChanged?.Invoke(new(args.FullPath, ShaderSourceFileChangeKind.Created));

    private void OnFileChanged(AssetFileChangeEventArgs args)
        => ShaderSourceFileChanged?.Invoke(new(args.FullPath, ShaderSourceFileChangeKind.Changed));

    private void OnFileDeleted(AssetFileChangeEventArgs args)
        => ShaderSourceFileChanged?.Invoke(new(args.FullPath, ShaderSourceFileChangeKind.Deleted));

    private void OnFileRenamed(AssetFileRenameEventArgs args)
        => ShaderSourceFileChanged?.Invoke(
            new(args.FullPath, ShaderSourceFileChangeKind.Renamed, args.OldFullPath));
}
