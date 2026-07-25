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
            if (ReferenceEquals(_current, value))
                return;

            if (_current is IRuntimeShaderChangeSource previousChangeSource)
                previousChangeSource.ShaderSourceFileChanged -= OnShaderSourceFileChanged;

            _current = value;
            if (value is IRuntimeShaderChangeSource nextChangeSource)
                nextChangeSource.ShaderSourceFileChanged += OnShaderSourceFileChanged;
        }
    }

    private static void OnShaderSourceFileChanged(ShaderSourceFileChange change)
        => ShaderSourceDependencyIndex.QueueFileChange(change);
}
