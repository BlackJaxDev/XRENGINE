using XREngine.Core.Files;

namespace XREngine.Rendering;

public interface IRuntimeShaderServices
{
    T? LoadAsset<T>(string filePath) where T : XRAsset, new();
    T LoadEngineAsset<T>(JobPriority priority, bool bypassJobThread, string assetRoot, string relativePath) where T : XRAsset, new();
    Task<T> LoadEngineAssetAsync<T>(JobPriority priority, bool bypassJobThread, string assetRoot, string relativePath) where T : XRAsset, new();
    void LogWarning(string message);
}

public static class RuntimeShaderServices
{
    public static IRuntimeShaderServices? Current { get; set; }
}
