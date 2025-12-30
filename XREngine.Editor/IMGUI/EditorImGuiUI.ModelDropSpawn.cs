using System;
using System.IO;
using System.Numerics;
using XREngine;
using XREngine.Components.Scene.Mesh;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Scene;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
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
            model = assets.Load<Model>(path);
            return model is not null;
        }
        catch
        {
            model = null;
            return false;
        }
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
