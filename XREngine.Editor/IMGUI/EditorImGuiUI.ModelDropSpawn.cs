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
    private static bool TryLoadPrefabAsset(string path, out XRPrefabSource? prefab)
    {
        prefab = null;

        if (string.IsNullOrWhiteSpace(path))
            return false;

        var assets = Engine.Assets;
        if (assets is null)
            return false;

        // Spec: dragging the prefab xrasset (native .asset).
        if (!string.Equals(Path.GetExtension(path), $".{AssetManager.AssetExtension}", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            prefab = assets.Load<XRPrefabSource>(path);
            if (prefab is not null)
                return true;

            if (!TryResolveConcreteAssetTypeFromHeader(path, out var concreteType))
                return false;

            if (!typeof(XRPrefabSource).IsAssignableFrom(concreteType))
                return false;

            if (assets.Load(path, concreteType) is XRPrefabSource concretePrefab)
            {
                prefab = concretePrefab;
                return true;
            }

            return false;
        }
        catch
        {
            prefab = null;
            return false;
        }
    }

    private static bool TryLoadModelAsset(string path, out Model? model)
    {
        model = null;

        if (string.IsNullOrWhiteSpace(path))
            return false;

        var assets = Engine.Assets;
        if (assets is null)
            return false;

        // Spec: dragging the Model xrasset (native .asset).
        if (!string.Equals(Path.GetExtension(path), $".{AssetManager.AssetExtension}", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            // First try the common case.
            model = assets.Load<Model>(path);
            if (model is not null)
                return true;

            // Some .asset files may be saved as a concrete type derived from Model.
            // Load<Model> deserializes as Model at the root, which can fail or lose important data.
            // Resolve the concrete type hint from the file header and load using the non-generic API.
            if (!TryResolveConcreteAssetTypeFromHeader(path, out var concreteType))
                return false;

            if (!typeof(Model).IsAssignableFrom(concreteType))
                return false;

            if (assets.Load(path, concreteType) is Model concreteModel)
            {
                model = concreteModel;
                return true;
            }

            return false;
        }
        catch
        {
            model = null;
            return false;
        }
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

    private static void SpawnPrefabNode(XRWorldInstance world, SceneNode? parent, XRPrefabSource prefab)
    {
        if (world is null || prefab is null)
            return;

        SceneNode? instance = Engine.Assets.InstantiatePrefab(prefab, world, parent, maintainWorldTransform: false);
        if (instance is null)
            return;

        Selection.SceneNode = instance;
        MarkSceneHierarchyDirty(instance, owningScene: null, world);
    }

    private static void SpawnModelNode(XRWorldInstance world, SceneNode? parent, Model model, string modelAssetPath)
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

        string name = model.Name;
        if (string.IsNullOrWhiteSpace(name))
            name = Path.GetFileNameWithoutExtension(modelAssetPath);
        if (string.IsNullOrWhiteSpace(name))
            name = SceneNode.DefaultName;

        node.Name = name;

        // Ensure local transform is at origin.
        if (node.Transform is XREngine.Scene.Transforms.Transform concrete)
            concrete.Translation = Vector3.Zero;

        var modelComponent = node.AddComponent<ModelComponent>();
        if (modelComponent is not null)
            modelComponent.Model = model;

        Selection.SceneNode = node;
        MarkSceneHierarchyDirty(node, owningScene: null, world);
    }
}
