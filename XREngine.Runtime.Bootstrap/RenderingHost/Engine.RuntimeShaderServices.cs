using XREngine.Core.Files;
using XREngine.Rendering;

namespace XREngine;

internal sealed class EngineRuntimeShaderServices : IRuntimeShaderServices
{
    public T? LoadAsset<T>(string filePath) where T : XRAsset, new()
        => Engine.Assets.Load<T>(filePath);

    public T LoadEngineAsset<T>(JobPriority priority, bool bypassJobThread, string assetRoot, string relativePath) where T : XRAsset, new()
        => Engine.Assets.LoadEngineAsset<T>(priority, bypassJobThread, assetRoot, relativePath);

    public Task<T> LoadEngineAssetAsync<T>(JobPriority priority, bool bypassJobThread, string assetRoot, string relativePath) where T : XRAsset, new()
        => Engine.Assets.LoadEngineAssetAsync<T>(priority, bypassJobThread, assetRoot, relativePath);

    public void LogWarning(string message)
        => Debug.LogWarning(message);
}
