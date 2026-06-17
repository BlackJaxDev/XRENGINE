namespace XREngine;

internal sealed class RuntimeAssetFacade
{
    public string EngineAssetsPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "Assets");
    public string GameAssetsPath { get; set; } = Path.Combine(Environment.CurrentDirectory, "Assets");

    public T? LoadEngineAsset<T>(JobPriority priority, params string[] pathParts)
        where T : class
        => default;
}
