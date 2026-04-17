using System;
using System.IO;
using System.Numerics;
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
    private static XRWorldInstance? _prefabPreviewWorld;
    private static SceneNode? _prefabPreviewParent;
    private static bool _prefabPreviewActive;
    private static bool _prefabPreviewHoveredThisFrame;

    private static bool TryLoadNativeAsset<TAsset>(string path, out TAsset? asset)
        where TAsset : XRAsset
    {
        asset = null;

        if (string.IsNullOrWhiteSpace(path))
            return false;

        var assets = Engine.Assets;
        if (assets is null)
            return false;

        if (!string.Equals(Path.GetExtension(path), $".{AssetManager.AssetExtension}", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            asset = assets.Load(path, typeof(TAsset)) as TAsset;
            if (asset is not null)
                return true;

            if (!TryResolveConcreteAssetTypeFromHeader(path, out var concreteType))
                return false;

            if (!typeof(TAsset).IsAssignableFrom(concreteType))
                return false;

            if (assets.Load(path, concreteType) is TAsset concreteAsset)
            {
                asset = concreteAsset;
                return true;
            }

            return false;
        }
        catch
        {
            asset = null;
            return false;
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

    private static bool TryLoadGeneratedThirdPartyAsset(string sourcePath, out XRAsset? asset)
    {
        asset = null;

        var assets = Engine.Assets;
        if (assets is null || !TryResolveGeneratedThirdPartyAssetPath(sourcePath, out string generatedAssetPath))
            return false;

        try
        {
            if (assets.GetAssetByOriginalPath(sourcePath) is XRAsset byOriginal
                && IsLinkedGeneratedThirdPartyAsset(byOriginal, sourcePath, generatedAssetPath))
            {
                asset = byOriginal;
                return true;
            }

            if (assets.GetAssetByPath(generatedAssetPath) is XRAsset byPath
                && IsLinkedGeneratedThirdPartyAsset(byPath, sourcePath, generatedAssetPath))
            {
                asset = byPath;
                return true;
            }

            if (!File.Exists(generatedAssetPath))
                return false;

            Type loadType = TryResolveConcreteAssetTypeFromHeader(generatedAssetPath, out var concreteType)
                ? concreteType
                : typeof(XRAsset);

            if (assets.Load(generatedAssetPath, loadType) is XRAsset loaded
                && IsLinkedGeneratedThirdPartyAsset(loaded, sourcePath, generatedAssetPath))
            {
                asset = loaded;
                return true;
            }

            return false;
        }
        catch
        {
            asset = null;
            return false;
        }
    }

    private static bool TryLoadPrefabAsset(string path, out XRPrefabSource? prefab)
    {
        prefab = null;

        if (TryLoadNativeAsset(path, out prefab))
            return prefab is not null && prefab.RootNode is not null;

        if (TryLoadGeneratedThirdPartyAsset(path, out XRAsset? generatedAsset)
            && generatedAsset is XRPrefabSource generatedPrefab
            && generatedPrefab.RootNode is not null)
        {
            prefab = generatedPrefab;
            return true;
        }

        return false;
    }

    private static bool TryLoadModelAsset(string path, out Model? model)
    {
        model = null;

        if (TryLoadNativeAsset(path, out model))
            return model is not null;

        if (TryLoadGeneratedThirdPartyAsset(path, out XRAsset? generatedAsset)
            && generatedAsset is Model generatedModel)
        {
            model = generatedModel;
            return true;
        }

        return false;
    }

    private static bool TryLoadMaterialAsset(string path, out XRMaterial? material)
    {
        return TryLoadNativeAsset(path, out material);
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

    private static bool TryImportAndLoadSpawnableAsset(string path, out XRPrefabSource? prefab, out Model? model)
    {
        prefab = null;
        model = null;

        if (TryLoadPrefabAsset(path, out prefab) || TryLoadModelAsset(path, out model))
            return true;

        var assets = Engine.Assets;
        if (assets is null || !TryResolveGeneratedThirdPartyAssetPath(path, out _))
            return false;

        try
        {
            if (!assets.ReimportThirdPartyFile(path))
                return false;
        }
        catch
        {
            return false;
        }

        return TryLoadPrefabAsset(path, out prefab) || TryLoadModelAsset(path, out model);
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

    private static bool TryHandleDroppedSpawnableAsset(XRWorldInstance world, SceneNode? parent, string path, Vector3? worldPosition = null)
    {
        if (world is null || string.IsNullOrWhiteSpace(path))
            return false;

        if (TryLoadPrefabAsset(path, out var prefab))
        {
            if (!TryFinalizePrefabPreview(world, parent, prefab!))
            {
                if (_prefabPreviewActive)
                    RevertPrefabPreview();

                EnqueueSceneEdit(() => SpawnPrefabNode(world, parent, prefab!, worldPosition));
            }

            return true;
        }

        if (TryLoadModelAsset(path, out var model))
        {
            if (_prefabPreviewActive)
                RevertPrefabPreview();

            EnqueueSceneEdit(() => SpawnModelNode(world, parent, model!, path, worldPosition));
            return true;
        }

        if (!TryImportAndLoadSpawnableAsset(path, out prefab, out model))
            return false;

        if (prefab is not null)
        {
            if (_prefabPreviewActive)
                RevertPrefabPreview();

            EnqueueSceneEdit(() => SpawnPrefabNode(world, parent, prefab, worldPosition));
            return true;
        }

        if (model is not null)
        {
            if (_prefabPreviewActive)
                RevertPrefabPreview();

            EnqueueSceneEdit(() => SpawnModelNode(world, parent, model, path, worldPosition));
            return true;
        }

        return false;
    }

    private static void SpawnPrefabNode(XRWorldInstance world, SceneNode? parent, XRPrefabSource prefab, Vector3? worldPosition = null)
    {
        if (world is null || prefab is null)
            return;

        SceneNode? instance = world.InstantiatePrefab(prefab, parent, maintainWorldTransform: false);
        if (instance is null)
            return;

        ApplySpawnWorldPosition(instance, worldPosition);
        FinalizeSpawnedPrefabNode(world, instance);
    }

    private static void FinalizeSpawnedPrefabNode(XRWorldInstance world, SceneNode instance)
    {
        if (world is null || instance is null)
            return;

        // Record structural undo
        var parentTfm = instance.Transform.Parent;
        using var interaction = Undo.BeginUserInteraction();
        using var scope = Undo.BeginChange("Spawn Prefab");
        Undo.TrackSceneNode(instance);
        Undo.RecordStructuralChange("Spawn Prefab",
            undoAction: () =>
            {
                if (parentTfm is not null)
                    parentTfm.RemoveChild(instance.Transform, Scene.Transforms.EParentAssignmentMode.Immediate);
                else
                    world.RootNodes.Remove(instance);
                instance.IsActiveSelf = false;
            },
            redoAction: () =>
            {
                if (parentTfm is not null)
                    instance.Transform.SetParent(parentTfm, false, Scene.Transforms.EParentAssignmentMode.Immediate);
                else
                    world.RootNodes.Add(instance);
                instance.IsActiveSelf = true;
                Undo.TrackSceneNode(instance);
            });

        Selection.SceneNode = instance;
        MarkSceneHierarchyDirty(instance, owningScene: null, world);
    }

    private static void UpdatePrefabPreview(XRWorldInstance world, SceneNode? parent, XRPrefabSource prefab, Vector3? worldPosition = null)
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

        SceneNode? instance = world.InstantiatePrefab(prefab, parent, maintainWorldTransform: false);
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

    private static bool TryFinalizePrefabPreview(XRWorldInstance world, SceneNode? parent, XRPrefabSource prefab)
    {
        if (!_prefabPreviewActive || _prefabPreviewNode is null)
            return false;

        if (!ReferenceEquals(_prefabPreviewWorld, world)
            || !ReferenceEquals(_prefabPreviewParent, parent)
            || !ReferenceEquals(_prefabPreviewPrefab, prefab))
        {
            return false;
        }

        SceneNode instance = _prefabPreviewNode;
        ClearPrefabPreviewState();
        FinalizeSpawnedPrefabNode(world, instance);
        return true;
    }

    private static void RevertPrefabPreview()
    {
        if (!_prefabPreviewActive || _prefabPreviewNode is null)
        {
            ClearPrefabPreviewState();
            return;
        }

        SceneNode node = _prefabPreviewNode;
        XRWorldInstance? world = _prefabPreviewWorld is not null
            ? _prefabPreviewWorld
            : node.World as XRWorldInstance;
        var parentTransform = node.Transform.Parent;

        if (parentTransform is not null)
            parentTransform.RemoveChild(node.Transform, XREngine.Scene.Transforms.EParentAssignmentMode.Immediate);
        else if (world is not null)
            world.RootNodes.Remove(node);

        node.IsActiveSelf = false;
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

    private static void SpawnModelNode(XRWorldInstance world, SceneNode? parent, Model model, string modelAssetPath, Vector3? worldPosition = null)
    {
        if (world is null || model is null)
            return;

        SceneNode node;
        if (parent is not null)
        {
            node = new SceneNode(parent);
        }
        else
        {
            node = new SceneNode(world);
            world.RootNodes.Add(node);
        }

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

        // Record structural undo
        var parentTfm = node.Transform.Parent;
        using var interaction = Undo.BeginUserInteraction();
        using var scope = Undo.BeginChange("Spawn Model");
        Undo.TrackSceneNode(node);
        Undo.RecordStructuralChange("Spawn Model",
            undoAction: () =>
            {
                if (parentTfm is not null)
                    parentTfm.RemoveChild(node.Transform, Scene.Transforms.EParentAssignmentMode.Immediate);
                else
                    world.RootNodes.Remove(node);
                node.IsActiveSelf = false;
            },
            redoAction: () =>
            {
                if (parentTfm is not null)
                    node.Transform.SetParent(parentTfm, false, Scene.Transforms.EParentAssignmentMode.Immediate);
                else
                    world.RootNodes.Add(node);
                node.IsActiveSelf = true;
                Undo.TrackSceneNode(node);
            });

        Selection.SceneNode = node;
        MarkSceneHierarchyDirty(node, owningScene: null, world);
    }
}
