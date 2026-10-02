using System.IO;
using System.Runtime.CompilerServices;
using XREngine.Components.Scene.Volumes;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Scene;

namespace XREngine;

internal sealed class EngineRuntimeSceneStreamingHostServices : IRuntimeSceneStreamingHostServices
{
    private readonly ConditionalWeakTable<RuntimeWorld, Dictionary<XRScene, SceneAttachment>> _attachedScenes = new();

    public async Task<IRuntimeSceneStreamingHandle?> LoadSceneAsync(string sceneAssetPath)
    {
        string? resolvedPath = ResolveSceneAssetPath(sceneAssetPath);
        if (string.IsNullOrWhiteSpace(resolvedPath))
            return null;

        IRuntimeAssetSource? source = DirectStorageIO.Source;
        XRScene? scene = await Engine.Assets.LoadAsync<XRScene>(resolvedPath).ConfigureAwait(false);
        if (scene is null)
            return null;
        if (source is IRuntimeAssetCatalog && !ReferenceEquals(source, DirectStorageIO.Source))
            throw new OperationCanceledException("AssetSource.StaleSession: the scene's catalog changed during loading.");
        if (source is IRuntimeScenePreparationSource preparation)
            await preparation.PrepareSceneAsync(scene, resolvedPath).ConfigureAwait(false);
        if (source is IRuntimeAssetCatalog && !ReferenceEquals(source, DirectStorageIO.Source))
            throw new OperationCanceledException("AssetSource.StaleSession: the scene's catalog changed during preparation.");
        return new SceneHandle(scene, source);
    }

    public bool AttachScene(IRuntimeWorldContext world, IRuntimeSceneStreamingHandle scene)
    {
        if (world is not RuntimeWorld runtimeWorld || scene is not SceneHandle handle)
            return false;

        if (runtimeWorld.IsDisposing)
            return false;
        if (handle.Source is IRuntimeAssetCatalog && !ReferenceEquals(handle.Source, DirectStorageIO.Source))
            return false;
        if (DirectStorageIO.Source is IRuntimeAssetCatalog && !handle.Scene.IsVisible)
            return false;
        if (handle.AttachedWorlds.Contains(runtimeWorld))
            return runtimeWorld.IsSceneReady(handle.Scene);
        if (!_attachedScenes.TryGetValue(runtimeWorld, out Dictionary<XRScene, SceneAttachment>? attached))
        {
            attached = new Dictionary<XRScene, SceneAttachment>(ReferenceEqualityComparer.Instance);
            _attachedScenes.Add(runtimeWorld, attached);
            runtimeWorld.Disposing += OnWorldDisposing;
        }
        if (attached.TryGetValue(handle.Scene, out SceneAttachment? state))
        {
            if (!state.Ready)
            {
                if (!state.CleanupPending)
                    return false;
                if (state.OwnsLoad && runtimeWorld.IsSceneLoaded(handle.Scene))
                    runtimeWorld.UnloadScene(handle.Scene);
                attached.Remove(handle.Scene);
            }
            else
            {
                if (!runtimeWorld.IsSceneReady(handle.Scene))
                    return false;
                state.Handles.Add(handle);
                handle.AttachedWorlds.Add(runtimeWorld);
                return true;
            }
        }
        if (runtimeWorld.IsSceneLoaded(handle.Scene) && !runtimeWorld.IsSceneReady(handle.Scene))
            throw new InvalidOperationException("Scene.CleanupPending: retry unloading this scene before attaching it again.");
        bool ownsLoad = !runtimeWorld.IsSceneLoaded(handle.Scene);
        state = new SceneAttachment(ownsLoad);
        attached.Add(handle.Scene, state);
        if (!ownsLoad)
        {
            state.Handles.Add(handle);
            handle.AttachedWorlds.Add(runtimeWorld);
            return true;
        }
        try
        {
            if (DirectStorageIO.Source is not IRuntimeAssetCatalog)
                handle.Scene.IsVisible = true;
            runtimeWorld.LoadScene(handle.Scene);
        }
        catch (Exception attachmentError)
        {
            try
            {
                if (runtimeWorld.IsSceneLoaded(handle.Scene))
                    runtimeWorld.UnloadScene(handle.Scene);
                attached.Remove(handle.Scene);
            }
            catch (Exception cleanupError)
            {
                state.CleanupPending = true;
                throw new AggregateException("Scene attachment and host cleanup both failed.", attachmentError, cleanupError);
            }
            throw;
        }
        state.Ready = true;
        state.Handles.Add(handle);
        handle.AttachedWorlds.Add(runtimeWorld);
        return true;

    }

    public bool DetachScene(IRuntimeWorldContext world, IRuntimeSceneStreamingHandle scene)
    {
        if (world is not RuntimeWorld runtimeWorld || scene is not SceneHandle handle)
            return false;

        if (!handle.AttachedWorlds.Contains(runtimeWorld) || handle.DetachingWorlds.Contains(runtimeWorld)
            || !_attachedScenes.TryGetValue(runtimeWorld, out Dictionary<XRScene, SceneAttachment>? attached)
            || !attached.TryGetValue(handle.Scene, out SceneAttachment? state)
            || !state.Handles.Contains(handle))
            return false;
        if (state.Handles.Count == 1 && state.OwnsLoad)
        {
            handle.DetachingWorlds.Add(runtimeWorld);
            try { runtimeWorld.UnloadScene(handle.Scene); }
            finally { handle.DetachingWorlds.Remove(runtimeWorld); }
        }
        state.Handles.Remove(handle);
        handle.AttachedWorlds.Remove(runtimeWorld);
        if (state.Handles.Count == 0)
            attached.Remove(handle.Scene);
        return true;
    }

    private void OnWorldDisposing(RuntimeWorld world)
    {
        if (_attachedScenes.TryGetValue(world, out Dictionary<XRScene, SceneAttachment>? attached))
        {
            foreach (SceneAttachment state in attached.Values)
                foreach (SceneHandle handle in state.Handles)
                {
                    handle.AttachedWorlds.Remove(world);
                    handle.DetachingWorlds.Remove(world);
                }
            attached.Clear();
            _attachedScenes.Remove(world);
        }
        world.Disposing -= OnWorldDisposing;
    }

    private static string? ResolveSceneAssetPath(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        string trimmed = input.Trim();
        if (DirectStorageIO.Source is IRuntimeAssetCatalog catalog)
        {
            string catalogInput = trimmed.Replace('\\', '/');
            string identity;
            if (catalogInput.StartsWith(AssetReferencePath.GamePrefix, StringComparison.OrdinalIgnoreCase))
                identity = "/game/" + catalogInput[AssetReferencePath.GamePrefix.Length..];
            else if (catalogInput.StartsWith(AssetReferencePath.EnginePrefix, StringComparison.OrdinalIgnoreCase))
                identity = "/engine/" + catalogInput[AssetReferencePath.EnginePrefix.Length..];
            else if (catalogInput.StartsWith("/game/", StringComparison.Ordinal)
                || catalogInput.StartsWith("/engine/", StringComparison.Ordinal))
                identity = catalogInput;
            else if (!Path.IsPathRooted(catalogInput) && !Path.IsPathFullyQualified(catalogInput)
                && !catalogInput.Contains(':'))
                identity = "/game/" + catalogInput;
            else
                throw new FileNotFoundException($"AssetSource.SceneNotPackaged: '{trimmed}' is not a portable scene identity.", trimmed);

            if (identity.Split('/').Skip(1).Any(static part => part is "" or "." or ".."))
                throw new FileNotFoundException($"AssetSource.SceneNotPackaged: '{trimmed}' is not a portable scene identity.", trimmed);
            if (!Path.HasExtension(identity))
                identity += $".{AssetManager.AssetExtension}";
            if (!catalog.TryGetAsset(identity, out RuntimeAssetCatalogEntry? entry) || entry.Path != identity)
                throw new FileNotFoundException($"AssetSource.SceneNotPackaged: '{identity}' is not in the runtime content catalog.", identity);
            if (entry.Encoding != RuntimeAssetEncoding.CookedBinary)
                throw new InvalidDataException($"AssetSource.SceneEncodingInvalid: '{identity}' must be a cooked scene.");
            return identity;
        }

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

    private sealed class SceneHandle(XRScene scene, IRuntimeAssetSource? source) : IRuntimeSceneStreamingHandle
    {
        public XRScene Scene { get; } = scene;
        public IRuntimeAssetSource? Source { get; } = source;
        public HashSet<RuntimeWorld> AttachedWorlds { get; } = new(ReferenceEqualityComparer.Instance);
        public HashSet<RuntimeWorld> DetachingWorlds { get; } = new(ReferenceEqualityComparer.Instance);
    }

    private sealed class SceneAttachment(bool ownsLoad)
    {
        public bool OwnsLoad { get; } = ownsLoad;
        public bool Ready { get; set; } = !ownsLoad;
        public bool CleanupPending { get; set; }
        public HashSet<SceneHandle> Handles { get; } = new(ReferenceEqualityComparer.Instance);
    }
}
