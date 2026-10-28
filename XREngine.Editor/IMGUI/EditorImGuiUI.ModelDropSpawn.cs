using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using XREngine;
using XREngine.Components.Scene.Mesh;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Scene;
using XREngine.Scene.Prefabs;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static SceneNode? _prefabPreviewNode;
    private static XRPrefabSource? _prefabPreviewPrefab;
    private static RuntimeWorld? _prefabPreviewWorld;
    private static SceneNode? _prefabPreviewParent;
    private static bool _prefabPreviewActive;
    private static bool _prefabPreviewHoveredThisFrame;
    private static readonly Dictionary<string, DroppedAssetLoadCacheEntry> _droppedAssetLoadCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _droppedAssetLoadCacheLock = new();

    private sealed class DroppedAssetLoadCacheEntry
    {
        public bool ImportAttempted { get; set; }
        public bool IsLoading { get; set; }
        public bool IsLoaded { get; set; }
        public string? ErrorMessage { get; set; }
        public Task? LoadTask { get; set; }
        public XRPrefabSource? Prefab { get; set; }
        public Model? Model { get; set; }
        public XRMaterial? Material { get; set; }
    }

    private readonly record struct DroppedAssetLoadResult(
        XRPrefabSource? Prefab,
        Model? Model,
        XRMaterial? Material,
        string? ErrorMessage);

    private static void ClearDroppedAssetLoadCache()
    {
        lock (_droppedAssetLoadCacheLock)
            _droppedAssetLoadCache.Clear();
    }

    private static void TrimDroppedAssetLoadCache()
    {
        if (_droppedAssetLoadCache.Count <= 256)
            return;

        List<string> completedKeys = [];
        foreach (var pair in _droppedAssetLoadCache)
        {
            if (!pair.Value.IsLoading)
                completedKeys.Add(pair.Key);
        }

        for (int i = 0; i < completedKeys.Count && _droppedAssetLoadCache.Count > 128; i++)
            _droppedAssetLoadCache.Remove(completedKeys[i]);
    }

    private static Task RequestDroppedAssetLoad(string path, bool allowImport)
    {
        string normalized = NormalizeDroppedAssetPath(path);
        if (string.IsNullOrWhiteSpace(normalized))
            return Task.CompletedTask;

        lock (_droppedAssetLoadCacheLock)
        {
            TrimDroppedAssetLoadCache();

            if (!_droppedAssetLoadCache.TryGetValue(normalized, out var entry))
            {
                entry = new DroppedAssetLoadCacheEntry();
                _droppedAssetLoadCache[normalized] = entry;
            }

            if (entry.IsLoading && entry.LoadTask is not null)
            {
                if (allowImport && !entry.ImportAttempted)
                {
                    entry.ImportAttempted = true;
                    Task importTask = entry.LoadTask.ContinueWith(
                        _ => LoadDroppedAssetCacheEntryIfUnresolvedAsync(normalized),
                        TaskScheduler.Default).Unwrap();
                    entry.LoadTask = importTask;
                    return importTask;
                }

                return entry.LoadTask;
            }

            if (entry.IsLoaded && (!allowImport || entry.ImportAttempted || HasResolvedDroppedAsset(entry)))
                return Task.CompletedTask;

            entry.IsLoading = true;
            entry.IsLoaded = false;
            entry.ErrorMessage = null;
            entry.ImportAttempted |= allowImport;
            Task task = LoadDroppedAssetCacheEntryAsync(normalized, allowImport);
            entry.LoadTask = task;

            return task;
        }
    }

    private static Task LoadDroppedAssetCacheEntryIfUnresolvedAsync(string normalizedPath)
    {
        lock (_droppedAssetLoadCacheLock)
        {
            if (!_droppedAssetLoadCache.TryGetValue(normalizedPath, out var entry))
                return Task.CompletedTask;

            if (HasResolvedDroppedAsset(entry))
                return Task.CompletedTask;

            entry.IsLoading = true;
            entry.IsLoaded = false;
            entry.ErrorMessage = null;
        }

        return LoadDroppedAssetCacheEntryAsync(normalizedPath, allowImport: true);
    }

    private static async Task LoadDroppedAssetCacheEntryAsync(string normalizedPath, bool allowImport)
    {
        DroppedAssetLoadResult result;
        try
        {
            result = await LoadDroppedAssetResultAsync(normalizedPath, allowImport).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = new DroppedAssetLoadResult(null, null, null, ex.Message);
            Debug.LogException(ex, $"Failed to load dropped asset '{normalizedPath}'.");
        }

        lock (_droppedAssetLoadCacheLock)
        {
            if (!_droppedAssetLoadCache.TryGetValue(normalizedPath, out var entry))
                return;

            entry.Prefab = result.Prefab;
            entry.Model = result.Model;
            entry.Material = result.Material;
            entry.ErrorMessage = result.ErrorMessage;
            entry.IsLoading = false;
            entry.IsLoaded = true;
        }
    }

    private static async Task<DroppedAssetLoadResult> LoadDroppedAssetResultAsync(string normalizedPath, bool allowImport)
    {
        var assets = Engine.Assets;
        if (assets is null)
            return new DroppedAssetLoadResult(null, null, null, "Asset manager is not available.");

        XRAsset? asset = await TryLoadDroppedNativeAssetAsync(assets, normalizedPath).ConfigureAwait(false);
        if (asset is null && TryResolveGeneratedThirdPartyAssetPath(normalizedPath, out string generatedAssetPath))
        {
            asset = ResolveCachedGeneratedThirdPartyAsset(assets, normalizedPath, generatedAssetPath);

            if (asset is null && allowImport)
            {
                bool imported = await assets.ReimportThirdPartyFileAsync(normalizedPath).ConfigureAwait(false);
                if (!imported)
                    return new DroppedAssetLoadResult(null, null, null, $"Failed to import {Path.GetFileName(normalizedPath)}.");

                asset = ResolveCachedGeneratedThirdPartyAsset(assets, normalizedPath, generatedAssetPath);
            }

            asset ??= await TryLoadDroppedNativeAssetAsync(assets, generatedAssetPath).ConfigureAwait(false);

            if (asset is not null && !IsLinkedGeneratedThirdPartyAsset(asset, normalizedPath, generatedAssetPath))
                asset = null;
        }

        return CreateDroppedAssetLoadResult(asset);
    }

    private static XRAsset? ResolveCachedGeneratedThirdPartyAsset(AssetManager assets, string sourcePath, string generatedAssetPath)
    {
        if (assets.GetAssetByOriginalPath(sourcePath) is XRAsset byOriginal
            && IsLinkedGeneratedThirdPartyAsset(byOriginal, sourcePath, generatedAssetPath))
        {
            return byOriginal;
        }

        if (assets.GetAssetByPath(generatedAssetPath) is XRAsset byPath
            && IsLinkedGeneratedThirdPartyAsset(byPath, sourcePath, generatedAssetPath))
        {
            return byPath;
        }

        return null;
    }

    private static async Task<XRAsset?> TryLoadDroppedNativeAssetAsync(AssetManager assets, string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !string.Equals(Path.GetExtension(path), $".{AssetManager.AssetExtension}", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        Type loadType = TryResolveConcreteAssetTypeFromHeader(path, out Type concreteType)
            ? concreteType
            : typeof(XRAsset);

        // A prior caller may have requested the file using an incompatible
        // type. Only trust the path cache when it agrees with the serialized
        // header; AssetManager.Load will evict a mismatched entry below.
        if (assets.GetAssetByPath(path) is XRAsset cached &&
            loadType.IsInstanceOfType(cached))
        {
            return cached;
        }

        if (typeof(XRPrefabSource).IsAssignableFrom(loadType))
        {
            return await assets
                .LoadPrefabWithReferencesAsync(path, bypassJobThread: true)
                .ConfigureAwait(false);
        }

        return await assets.LoadAsync(path, loadType).ConfigureAwait(false);
    }

    private static DroppedAssetLoadResult CreateDroppedAssetLoadResult(XRAsset? asset)
    {
        if (asset is XRPrefabSource prefab && prefab.RootNode is not null)
            return new DroppedAssetLoadResult(prefab, null, null, null);

        if (asset is Model model)
            return new DroppedAssetLoadResult(null, model, null, null);

        if (asset is XRMaterial material)
            return new DroppedAssetLoadResult(null, null, material, null);

        return new DroppedAssetLoadResult(null, null, null, asset is null ? "No spawnable asset was found." : null);
    }

    private static bool TryGetDroppedAssetLoadResult(string path, out DroppedAssetLoadResult result)
    {
        string normalized = NormalizeDroppedAssetPath(path);
        lock (_droppedAssetLoadCacheLock)
        {
            if (_droppedAssetLoadCache.TryGetValue(normalized, out var entry) && entry.IsLoaded)
            {
                result = new DroppedAssetLoadResult(entry.Prefab, entry.Model, entry.Material, entry.ErrorMessage);
                return true;
            }
        }

        result = default;
        return false;
    }

    private static bool HasResolvedDroppedAsset(DroppedAssetLoadCacheEntry entry)
        => entry.Prefab is not null || entry.Model is not null || entry.Material is not null;

    private static string NormalizeDroppedAssetPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private static bool TryResolveGeneratedThirdPartyAssetPath(string sourcePath, out string generatedAssetPath)
    {
        generatedAssetPath = string.Empty;

        if (string.IsNullOrWhiteSpace(sourcePath))
            return false;

        var assets = Engine.Assets;
        if (assets is null || string.IsNullOrWhiteSpace(assets.GameAssetsPath))
            return false;

        string normalizedSource = Path.GetFullPath(sourcePath);
        if (!File.Exists(normalizedSource)
            || string.Equals(Path.GetExtension(normalizedSource), $".{AssetManager.AssetExtension}", StringComparison.OrdinalIgnoreCase))
            return false;

        string normalizedAssetsRoot = Path.GetFullPath(assets.GameAssetsPath);
        string relativePath = Path.GetRelativePath(normalizedAssetsRoot, normalizedSource);
        if (relativePath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
            return false;

        string? directory = Path.GetDirectoryName(normalizedSource);
        string name = Path.GetFileNameWithoutExtension(normalizedSource);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(name))
            return false;

        generatedAssetPath = Path.GetFullPath(Path.Combine(directory, $"{name}.{AssetManager.AssetExtension}"));
        return true;
    }

    private static bool IsLinkedGeneratedThirdPartyAsset(XRAsset asset, string sourcePath, string generatedAssetPath)
    {
        if (asset is null
            || string.IsNullOrWhiteSpace(asset.OriginalPath)
            || string.IsNullOrWhiteSpace(asset.FilePath))
            return false;

        string normalizedSource = Path.GetFullPath(sourcePath);
        string normalizedOriginalPath = Path.GetFullPath(asset.OriginalPath);
        string normalizedGeneratedAssetPath = Path.GetFullPath(generatedAssetPath);
        string normalizedAssetPath = Path.GetFullPath(asset.FilePath);

        return string.Equals(normalizedOriginalPath, normalizedSource, StringComparison.OrdinalIgnoreCase)
            && string.Equals(normalizedAssetPath, normalizedGeneratedAssetPath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryLoadPrefabAsset(string path, out XRPrefabSource? prefab)
    {
        prefab = null;

        _ = RequestDroppedAssetLoad(path, allowImport: false);
        if (TryGetDroppedAssetLoadResult(path, out var result) && result.Prefab is not null)
        {
            prefab = result.Prefab;
            return true;
        }

        return false;
    }

    private static bool TryLoadModelAsset(string path, out Model? model)
    {
        model = null;

        _ = RequestDroppedAssetLoad(path, allowImport: false);
        if (TryGetDroppedAssetLoadResult(path, out var result) && result.Model is not null)
        {
            model = result.Model;
            return true;
        }

        return false;
    }

    private static bool TryLoadMaterialAsset(string path, out XRMaterial? material)
    {
        material = null;

        _ = RequestDroppedAssetLoad(path, allowImport: false);
        if (TryGetDroppedAssetLoadResult(path, out var result) && result.Material is not null)
        {
            material = result.Material;
            return true;
        }

        return false;
    }

    private static bool TryResolveConcreteAssetTypeFromHeader(string assetPath, out Type type)
    {
        type = typeof(XRAsset);

        if (string.IsNullOrWhiteSpace(assetPath))
            return false;

        string? hint = null;
        try
        {
            foreach (var line in File.ReadLines(assetPath))
            {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith("__assetType:", StringComparison.Ordinal))
                    continue;

                hint = trimmed.Substring("__assetType:".Length).Trim();
                hint = hint.Trim('"', '\'');
                break;
            }
        }
        catch
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(hint))
            return false;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var resolved = assembly.GetType(hint, throwOnError: false, ignoreCase: false);
            if (resolved is not null && typeof(XRAsset).IsAssignableFrom(resolved))
            {
                type = resolved;
                return true;
            }
        }

        var direct = Type.GetType(hint, throwOnError: false);
        if (direct is not null && typeof(XRAsset).IsAssignableFrom(direct))
        {
            type = direct;
            return true;
        }

        return false;
    }

    private static Vector3? GetViewportDropWorldPosition()
    {
        if (Engine.State.MainPlayer?.ControlledPawnComponent is not EditorFlyingCameraPawnComponent pawn)
            return null;

        return pawn.WorldDragPoint;
    }

    private static void ApplySpawnWorldPosition(SceneNode node, Vector3? worldPosition)
    {
        if (node is null || !worldPosition.HasValue)
            return;

        Matrix4x4 worldMatrix = node.Transform.WorldMatrix;
        worldMatrix.Translation = worldPosition.Value;
        node.Transform.DeriveWorldMatrix(worldMatrix);
    }

    /// <summary>
    /// Resolves and spawns an asset through the same path used by hierarchy and
    /// viewport drag/drop. Kept internal so editor automation can exercise the
    /// user-facing workflow without duplicating its loading or prefab logic.
    /// </summary>
    internal static bool TryHandleDroppedSpawnableAsset(RuntimeWorld world, SceneNode? parent, string path,
        Vector3? worldPosition = null, Func<bool>? canApply = null)
    {
        if (world is null || string.IsNullOrWhiteSpace(path) || canApply?.Invoke() == false ||
            !IsValidDropSpawnTarget(world, parent))
            return false;

        if (TryGetDroppedAssetLoadResult(path, out var cached))
        {
            if (cached.Prefab is not null)
                return SpawnDroppedPrefab(world, parent, cached.Prefab, worldPosition, enqueueSceneEdit: true,
                    canApply: canApply);

            if (cached.Model is not null)
                return SpawnDroppedModel(world, parent, cached.Model, path, worldPosition, enqueueSceneEdit: true,
                    canApply: canApply);
        }

        QueueDroppedSpawnableAssetSpawn(world, parent, path, worldPosition, canApply);
        return true;
    }

    /// <summary>
    /// Accepts the world root, a live node in the world, or a node of a hidden scene
    /// that the world still owns.
    /// </summary>
    private static bool IsValidDropSpawnTarget(RuntimeWorld world, SceneNode? parent)
        => !world.IsDisposing && (parent is null || HierarchySceneOwnership.CanAuthor(parent, world));

    private static bool TryHandleDroppedAsset(RuntimeWorld world, SceneNode? parent, string path, Vector3? worldPosition = null)
    {
        if (world is null || string.IsNullOrWhiteSpace(path))
            return false;

        if (TryGetDroppedAssetLoadResult(path, out var cached))
        {
            if (cached.Material is not null)
            {
                EnqueueSceneEdit(() =>
                {
                    if (_prefabPreviewActive)
                        RevertPrefabPreview();
                    ClearMaterialPreviewState();
                    TryApplyMaterialDropToHoveredSubmesh(world, cached.Material);
                });
                return true;
            }

            if (cached.Prefab is not null)
                return SpawnDroppedPrefab(world, parent, cached.Prefab, worldPosition, enqueueSceneEdit: true);

            if (cached.Model is not null)
                return SpawnDroppedModel(world, parent, cached.Model, path, worldPosition, enqueueSceneEdit: true);
        }

        QueueDroppedAssetApply(world, parent, path, worldPosition);
        return true;
    }

    private static bool SpawnDroppedPrefab(RuntimeWorld world, SceneNode? parent, XRPrefabSource prefab,
        Vector3? worldPosition, bool enqueueSceneEdit, Func<bool>? canApply = null)
    {
        if (prefab is null || canApply?.Invoke() == false || !IsValidDropSpawnTarget(world, parent))
            return false;

        if (enqueueSceneEdit)
        {
            EnqueueSceneEdit(() => SpawnDroppedPrefab(world, parent, prefab, worldPosition,
                enqueueSceneEdit: false, canApply: canApply));
            return true;
        }

        if (!TryFinalizePrefabPreview(world, parent, prefab))
        {
            if (_prefabPreviewActive)
                RevertPrefabPreview();

            return SpawnPrefabNode(world, parent, prefab, worldPosition);
        }

        return true;
    }

    private static bool SpawnDroppedModel(RuntimeWorld world, SceneNode? parent, Model model, string path,
        Vector3? worldPosition, bool enqueueSceneEdit, Func<bool>? canApply = null)
    {
        if (model is null || canApply?.Invoke() == false || !IsValidDropSpawnTarget(world, parent))
            return false;

        if (enqueueSceneEdit)
        {
            EnqueueSceneEdit(() => SpawnDroppedModel(world, parent, model, path, worldPosition,
                enqueueSceneEdit: false, canApply: canApply));
            return true;
        }

        if (_prefabPreviewActive)
            RevertPrefabPreview();

        return SpawnModelNode(world, parent, model, path, worldPosition);
    }

    private static void QueueDroppedSpawnableAssetSpawn(RuntimeWorld world, SceneNode? parent, string path,
        Vector3? worldPosition, Func<bool>? canApply)
    {
        Guid trackingId = EditorJobTracker.TrackOperation($"Spawn {Path.GetFileName(path)}", "Loading dropped asset...");
        _ = SpawnDroppedAssetWhenReadyAsync(world, parent, path, worldPosition, trackingId, canApply);
    }

    private static async Task SpawnDroppedAssetWhenReadyAsync(RuntimeWorld world, SceneNode? parent, string path,
        Vector3? worldPosition, Guid trackingId, Func<bool>? canApply)
    {
        string? error = await TrySpawnDroppedAssetThroughDropPathAsync(
            world,
            parent,
            path,
            worldPosition, canApply).ConfigureAwait(false);
        if (error is null)
            EditorJobTracker.Complete(trackingId, $"Spawned {Path.GetFileName(path)}");
        else
            EditorJobTracker.Fault(trackingId, error);
    }

    /// <summary>
    /// Completes the asynchronous portion of a prefab/model drop and waits for
    /// its scene edit to be applied. The hierarchy/viewport handlers and MCP
    /// automation share this loader and the same final spawn methods.
    /// </summary>
    internal static async Task<string?> TrySpawnDroppedAssetThroughDropPathAsync(
        RuntimeWorld world,
        SceneNode? parent,
        string path,
        Vector3? worldPosition,
        Func<bool>? canApply = null)
    {
        try
        {
            await RequestDroppedAssetLoad(path, allowImport: true).ConfigureAwait(false);
            if (!TryGetDroppedAssetLoadResult(path, out DroppedAssetLoadResult result))
                return $"Unable to resolve {Path.GetFileName(path)}.";

            if (result.Prefab is null && result.Model is null)
                return result.ErrorMessage ?? $"No spawnable asset found for {Path.GetFileName(path)}.";

            TaskCompletionSource<string?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            EnqueueSceneEdit(() =>
            {
                try
                {
                    if (canApply?.Invoke() == false || !IsValidDropSpawnTarget(world, parent))
                    {
                        completion.TrySetResult("The scene target changed before the dropped asset was ready.");
                        return;
                    }
                    bool spawned = result.Prefab is not null
                        ? SpawnDroppedPrefab(world, parent, result.Prefab, worldPosition, enqueueSceneEdit: false,
                            canApply: canApply)
                        : SpawnDroppedModel(world, parent, result.Model!, path, worldPosition, enqueueSceneEdit: false,
                            canApply: canApply);
                    completion.TrySetResult(spawned
                        ? null
                        : $"The editor could not spawn {Path.GetFileName(path)}.");
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex, $"Failed to spawn dropped asset '{path}'.");
                    completion.TrySetResult(ex.Message);
                }
            });

            return await completion.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex, $"Failed to load dropped asset '{path}'.");
            return ex.Message;
        }
    }

    private static void QueueDroppedAssetApply(RuntimeWorld world, SceneNode? parent, string path, Vector3? worldPosition)
    {
        Guid trackingId = EditorJobTracker.TrackOperation($"Drop {Path.GetFileName(path)}", "Loading dropped asset...");
        _ = ApplyDroppedAssetWhenReadyAsync(world, parent, path, worldPosition, trackingId);
    }

    private static async Task ApplyDroppedAssetWhenReadyAsync(RuntimeWorld world, SceneNode? parent, string path, Vector3? worldPosition, Guid trackingId)
    {
        try
        {
            await RequestDroppedAssetLoad(path, allowImport: true).ConfigureAwait(false);
            if (!TryGetDroppedAssetLoadResult(path, out var result))
            {
                EditorJobTracker.Fault(trackingId, $"Unable to resolve {Path.GetFileName(path)}.");
                return;
            }

            if (result.Material is not null)
            {
                EnqueueSceneEdit(() =>
                {
                    if (_prefabPreviewActive)
                        RevertPrefabPreview();
                    ClearMaterialPreviewState();
                    TryApplyMaterialDropToHoveredSubmesh(world, result.Material);
                    EditorJobTracker.Complete(trackingId, $"Applied {Path.GetFileName(path)}");
                });
                return;
            }

            if (result.Prefab is not null)
            {
                EnqueueSceneEdit(() =>
                {
                    SpawnDroppedPrefab(world, parent, result.Prefab, worldPosition, enqueueSceneEdit: false);
                    EditorJobTracker.Complete(trackingId, $"Spawned {Path.GetFileName(path)}");
                });
                return;
            }

            if (result.Model is not null)
            {
                EnqueueSceneEdit(() =>
                {
                    SpawnDroppedModel(world, parent, result.Model, path, worldPosition, enqueueSceneEdit: false);
                    EditorJobTracker.Complete(trackingId, $"Spawned {Path.GetFileName(path)}");
                });
                return;
            }

            EditorJobTracker.Fault(trackingId, result.ErrorMessage ?? $"No supported drop action found for {Path.GetFileName(path)}.");
        }
        catch (Exception ex)
        {
            EditorJobTracker.Fault(trackingId, ex.Message);
            Debug.LogException(ex, $"Failed to apply dropped asset '{path}'.");
        }
    }

    private static bool SpawnPrefabNode(RuntimeWorld world, SceneNode? parent, XRPrefabSource prefab, Vector3? worldPosition = null)
    {
        if (world is null || prefab is null)
            return false;

        // Build the instance detached; the structural edit owns its attachment and history.
        SceneNode instance = SceneNodePrefabUtility.Instantiate(prefab, null, null, maintainWorldTransform: false);
        instance.IsActiveSelf = false;
        ApplySpawnWorldPosition(instance, worldPosition);
        return PlaceSpawnedNode("Spawn Prefab", world, parent, instance, worldPosition.HasValue);
    }

    /// <summary>
    /// Places a detached spawned node under the parent, or as a root of the default spawn scene,
    /// through the canonical hierarchy edit. A world position given before placement is kept.
    /// </summary>
    private static bool PlaceSpawnedNode(string description, RuntimeWorld world, SceneNode? parent,
        SceneNode node, bool preserveWorld)
        => HierarchySpawn.Place(description, world, parent, parent is null ? ResolveDefaultSpawnScene(world) : null,
            node, preserveWorld && parent is not null);

    private static XRScene? ResolveDefaultSpawnScene(RuntimeWorld world)
    {
        var scenes = world.TargetWorld?.Scenes;
        if (scenes is null || scenes.Count == 0)
            return null;

        return scenes.FirstOrDefault(static scene => scene is not null && scene.IsVisible && !scene.IsEditorOnly)
            ?? scenes.FirstOrDefault(static scene => scene is not null && !scene.IsEditorOnly)
            ?? scenes.FirstOrDefault();
    }

    private static void UpdatePrefabPreview(RuntimeWorld world, SceneNode? parent, XRPrefabSource prefab, Vector3? worldPosition = null)
    {
        if (world is null || prefab is null)
            return;

        _prefabPreviewHoveredThisFrame = true;

        if (_prefabPreviewActive
            && _prefabPreviewNode is not null
            && ReferenceEquals(_prefabPreviewWorld, world)
            && ReferenceEquals(_prefabPreviewParent, parent)
            && ReferenceEquals(_prefabPreviewPrefab, prefab))
        {
            ApplySpawnWorldPosition(_prefabPreviewNode, worldPosition);
            return;
        }

        RevertPrefabPreview();

        SceneNode? instance = SceneNodePrefabUtility.Instantiate(prefab, world, parent, maintainWorldTransform: false);
        if (instance is null)
            return;

        ApplySpawnWorldPosition(instance, worldPosition);
        _prefabPreviewNode = instance;
        _prefabPreviewPrefab = prefab;
        _prefabPreviewWorld = world;
        _prefabPreviewParent = parent;
        _prefabPreviewActive = true;
        MarkSceneHierarchyDirty(instance, owningScene: null, world);
    }

    private static bool TryFinalizePrefabPreview(RuntimeWorld world, SceneNode? parent, XRPrefabSource prefab)
    {
        if (!_prefabPreviewActive || _prefabPreviewNode is null)
            return false;

        if (!ReferenceEquals(_prefabPreviewWorld, world)
            || !ReferenceEquals(_prefabPreviewParent, parent)
            || !ReferenceEquals(_prefabPreviewPrefab, prefab))
        {
            return false;
        }

        // Detach the preview first so the spawn edit records the complete placement. The
        // preview keeps its local transform, so attachment does not preserve the world matrix.
        SceneNode instance = _prefabPreviewNode;
        DetachPrefabPreview(instance, world);
        ClearPrefabPreviewState();
        PlaceSpawnedNode("Spawn Prefab", world, parent, instance, preserveWorld: false);
        return true;
    }

    private static void DetachPrefabPreview(SceneNode node, RuntimeWorld? world)
    {
        var parentTransform = node.Transform.Parent;
        if (parentTransform is not null)
            parentTransform.RemoveChild(node.Transform, XREngine.Scene.Transforms.EParentAssignmentMode.Immediate);
        else if (world is not null)
            world.RootNodes.Remove(node);
        node.IsActiveSelf = false;
    }

    private static void RevertPrefabPreview()
    {
        if (!_prefabPreviewActive || _prefabPreviewNode is null)
        {
            ClearPrefabPreviewState();
            return;
        }

        SceneNode node = _prefabPreviewNode;
        RuntimeWorld? world = _prefabPreviewWorld is not null
            ? _prefabPreviewWorld
            : node.World as RuntimeWorld;
        DetachPrefabPreview(node, world);
        MarkSceneHierarchyDirty(node, owningScene: null, world);
        ClearPrefabPreviewState();
    }

    private static void ClearPrefabPreviewState()
    {
        _prefabPreviewNode = null;
        _prefabPreviewPrefab = null;
        _prefabPreviewWorld = null;
        _prefabPreviewParent = null;
        _prefabPreviewActive = false;
    }

    private static bool SpawnModelNode(RuntimeWorld world, SceneNode? parent, Model model, string modelAssetPath, Vector3? worldPosition = null)
    {
        if (world is null || model is null)
            return false;

        // Build the node detached; the structural edit owns its attachment and history.
        SceneNode node = new() { IsActiveSelf = false };
        string? name = model.Name;
        if (string.IsNullOrWhiteSpace(name))
            name = Path.GetFileNameWithoutExtension(modelAssetPath);
        if (string.IsNullOrWhiteSpace(name))
            name = SceneNode.DefaultName;

        node.Name = name;

        ApplySpawnWorldPosition(node, worldPosition);
        if (!worldPosition.HasValue && node.Transform is XREngine.Scene.Transforms.Transform concrete)
            concrete.Translation = Vector3.Zero;

        var modelComponent = node.AddComponent<ModelComponent>();
        if (modelComponent is not null)
            modelComponent.Model = model;

        return PlaceSpawnedNode("Spawn Model", world, parent, node, worldPosition.HasValue);
    }
}
