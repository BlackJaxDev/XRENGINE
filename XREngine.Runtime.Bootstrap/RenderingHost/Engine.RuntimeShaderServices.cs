using XREngine.Core.Files;
using XREngine.Rendering;

namespace XREngine;

internal sealed class EngineRuntimeShaderServices : IRuntimeShaderServices, IRuntimeShaderChangeSource
{
    public EngineRuntimeShaderServices()
    {
        Engine.Assets.EngineFileCreated += OnFileCreated;
        Engine.Assets.EngineFileChanged += OnFileChanged;
        Engine.Assets.EngineFileDeleted += OnFileDeleted;
        Engine.Assets.EngineFileRenamed += OnFileRenamed;
        Engine.Assets.GameFileCreated += OnFileCreated;
        Engine.Assets.GameFileChanged += OnFileChanged;
        Engine.Assets.GameFileDeleted += OnFileDeleted;
        Engine.Assets.GameFileRenamed += OnFileRenamed;
    }

    public event Action<ShaderSourceFileChange>? ShaderSourceFileChanged;

    public T? LoadAsset<T>(string filePath) where T : XRAsset, new()
        => Engine.Assets.Load<T>(filePath);

    public T LoadEngineAsset<T>(JobPriority priority, bool bypassJobThread, string assetRoot, string relativePath) where T : XRAsset, new()
        => Engine.Assets.LoadEngineAsset<T>(priority, bypassJobThread, assetRoot, relativePath);

    public Task<T> LoadEngineAssetAsync<T>(JobPriority priority, bool bypassJobThread, string assetRoot, string relativePath) where T : XRAsset, new()
        => Engine.Assets.LoadEngineAssetAsync<T>(priority, bypassJobThread, assetRoot, relativePath);

    public void LogWarning(string message)
        => Debug.LogWarning(message);

    private void OnFileCreated(FileSystemEventArgs args)
        => ShaderSourceFileChanged?.Invoke(new(args.FullPath, ShaderSourceFileChangeKind.Created));

    private void OnFileChanged(FileSystemEventArgs args)
        => ShaderSourceFileChanged?.Invoke(new(args.FullPath, ShaderSourceFileChangeKind.Changed));

    private void OnFileDeleted(FileSystemEventArgs args)
        => ShaderSourceFileChanged?.Invoke(new(args.FullPath, ShaderSourceFileChangeKind.Deleted));

    private void OnFileRenamed(RenamedEventArgs args)
        => ShaderSourceFileChanged?.Invoke(
            new(args.FullPath, ShaderSourceFileChangeKind.Renamed, args.OldFullPath));
}
