using XREngine.Core.Files;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine;

namespace XREngine.UnitTests.Rendering;

/// <summary>Supplies shader assets for managed pipeline construction tests.</summary>
internal sealed class PipelineTestShaderServices : IRuntimeShaderServices
{
    public T? LoadAsset<T>(string filePath) where T : XRAsset, new()
        => new T();

    public T LoadEngineAsset<T>(JobPriority priority, bool bypassJobThread, string assetRoot, string relativePath)
        where T : XRAsset, new()
        => CreateAsset<T>(relativePath);

    public Task<T> LoadEngineAssetAsync<T>(JobPriority priority, bool bypassJobThread, string assetRoot,
        string relativePath) where T : XRAsset, new()
        => Task.FromResult(CreateAsset<T>(relativePath));

    public void LogWarning(string message) { }

    private static T CreateAsset<T>(string relativePath) where T : XRAsset, new()
    {
        if (typeof(T) != typeof(XRShader))
            return new T();

        TextFile source = TextFile.FromText("void main() {}\n");
        source.FilePath = relativePath;
        XRShader shader = new(EShaderType.Fragment, source) { FilePath = relativePath };
        return (T)(XRAsset)shader;
    }
}
