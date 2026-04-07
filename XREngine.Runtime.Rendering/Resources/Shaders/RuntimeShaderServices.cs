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
    private static IRuntimeShaderServices? _current;
    public static IRuntimeShaderServices? Current
    {
        get => _current;
        set
        {
            var prev = _current;
            _current = value;
            // TEMPORARY DIAGNOSTIC: trace who changes the service
            if (prev?.GetType() != value?.GetType())
            {
                var st = new System.Diagnostics.StackTrace(true).ToString().Replace("\n", " | ").Replace("\r", "");
                Console.Error.WriteLine($"[RSS-DIAG] {prev?.GetType().Name ?? "null"} → {value?.GetType().Name ?? "null"} STACK: {st}");
            }
        }
    }
}
