using System.IO;
using XREngine.Components.Scene.Volumes;
using XREngine.Rendering;
using XREngine.Scene;

namespace XREngine;

internal sealed class EngineRuntimeSceneStreamingHostServices : IRuntimeSceneStreamingHostServices
{
    public async Task<IRuntimeSceneStreamingHandle?> LoadSceneAsync(string sceneAssetPath)
    {
        string? resolvedPath = ResolveSceneAssetPath(sceneAssetPath);
        if (string.IsNullOrWhiteSpace(resolvedPath))
            return null;

        XRScene? scene = await Engine.Assets.LoadAsync<XRScene>(resolvedPath).ConfigureAwait(false);
        return scene is null ? null : new SceneHandle(scene);
    }

    public bool AttachScene(IRuntimeWorldContext world, IRuntimeSceneStreamingHandle scene)
    {
        if (world is not XRWorldInstance worldInstance || scene is not SceneHandle handle)
            return false;

        handle.Scene.IsVisible = true;
        worldInstance.LoadScene(handle.Scene);
        return true;
    }

    public bool DetachScene(IRuntimeWorldContext world, IRuntimeSceneStreamingHandle scene)
    {
        if (world is not XRWorldInstance worldInstance || scene is not SceneHandle handle)
            return false;

        worldInstance.UnloadScene(handle.Scene);
        return true;
    }

    private static string? ResolveSceneAssetPath(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        string trimmed = input.Trim();
        if (Path.IsPathFullyQualified(trimmed) || Path.IsPathRooted(trimmed))
            return trimmed;

        string relativePath = trimmed.Replace('/', Path.DirectorySeparatorChar);
        string fromGameAssets = Path.Combine(Engine.Assets.GameAssetsPath, relativePath);
        if (File.Exists(fromGameAssets))
            return fromGameAssets;

        if (Path.HasExtension(fromGameAssets))
            return fromGameAssets;

        return $"{fromGameAssets}.{AssetManager.AssetExtension}";
    }

    private sealed class SceneHandle(XRScene scene) : IRuntimeSceneStreamingHandle
    {
        public XRScene Scene { get; } = scene;
    }
}
