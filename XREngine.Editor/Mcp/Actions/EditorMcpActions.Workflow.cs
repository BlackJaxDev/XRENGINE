using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using XREngine;
using XREngine.Components.Mesh.Shapes;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Editor;
using XREngine.Modeling;
using XREngine.Rendering;
using XREngine.Rendering.Modeling;
using XREngine.Rendering.Models;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor.Mcp
{
    public sealed partial class EditorMcpActions
    {
        [XRMcp(Name = "undo")]
        [Description("Undo the most recent editor change.")]
        public static Task<McpToolResponse> UndoAsync(McpToolContext context)
        {
            bool success = Undo.TryUndo();
            return Task.FromResult(success
                ? new McpToolResponse("Undo applied.")
                : new McpToolResponse("Nothing to undo.", isError: true));
        }

        [XRMcp(Name = "redo")]
        [Description("Redo the most recently undone editor change.")]
        public static Task<McpToolResponse> RedoAsync(McpToolContext context)
        {
            bool success = Undo.TryRedo();
            return Task.FromResult(success
                ? new McpToolResponse("Redo applied.")
                : new McpToolResponse("Nothing to redo.", isError: true));
        }

        [XRMcp(Name = "clear_selection")]
        [Description("Clear the current scene-node selection.")]
        public static Task<McpToolResponse> ClearSelectionAsync(McpToolContext context)
        {
            Selection.Clear();
            return Task.FromResult(new McpToolResponse("Selection cleared."));
        }

        [XRMcp(Name = "delete_selected_nodes")]
        [Description("Delete all currently selected scene nodes.")]
        public static Task<McpToolResponse> DeleteSelectedNodesAsync(McpToolContext context)
        {
            var targets = Selection.SceneNodes
                .Where(node => node.World == context.WorldInstance)
                .Distinct()
                .ToArray();

            if (targets.Length == 0)
                return Task.FromResult(new McpToolResponse("No selected nodes found in the active world.", isError: true));

            var world = context.WorldInstance;
            // Capture pre-deletion state for each node
            var nodeStates = targets.Select(node => new
            {
                Node = node,
                ParentTransform = node.Transform.Parent,
                WasActive = node.IsActiveSelf
            }).ToArray();

            // Soft-delete all nodes
            foreach (var state in nodeStates)
            {
                if (state.ParentTransform is not null)
                    state.ParentTransform.RemoveChild(state.Node.Transform, EParentAssignmentMode.Immediate);
                else
                    world.RootNodes.Remove(state.Node);

                state.Node.IsActiveSelf = false;
            }

            Selection.Clear();

            // Record structural undo
            using var interaction = Undo.BeginUserInteraction();
            using var scope = Undo.BeginChange("MCP Delete Selected Nodes");
            Undo.RecordStructuralChange($"Delete {targets.Length} node(s)",
                undoAction: () =>
                {
                    foreach (var state in nodeStates)
                    {
                        if (state.ParentTransform is not null)
                            state.Node.Transform.SetParent(state.ParentTransform, false, EParentAssignmentMode.Immediate);
                        else
                            world.RootNodes.Add(state.Node);
                        state.Node.IsActiveSelf = state.WasActive;
                        Undo.TrackSceneNode(state.Node);
                    }
                },
                redoAction: () =>
                {
                    foreach (var state in nodeStates)
                    {
                        if (state.ParentTransform is not null)
                            state.ParentTransform.RemoveChild(state.Node.Transform, EParentAssignmentMode.Immediate);
                        else
                            world.RootNodes.Remove(state.Node);
                        state.Node.IsActiveSelf = false;
                    }
                });

            return Task.FromResult(new McpToolResponse($"Deleted {targets.Length} selected node(s)."));
        }

        [XRMcp(Name = "enter_play_mode")]
        [Description("Enter play mode.")]
        public static Task<McpToolResponse> EnterPlayModeAsync(McpToolContext context)
        {
            if (EditorState.InPlayMode)
                return Task.FromResult(new McpToolResponse("Already in play mode."));

            EditorState.EnterPlayMode();
            return Task.FromResult(new McpToolResponse("Play mode transition requested."));
        }

        [XRMcp(Name = "exit_play_mode")]
        [Description("Exit play mode.")]
        public static Task<McpToolResponse> ExitPlayModeAsync(McpToolContext context)
        {
            if (EditorState.InEditMode)
                return Task.FromResult(new McpToolResponse("Already in edit mode."));

            EditorState.ExitPlayMode();
            return Task.FromResult(new McpToolResponse("Exit play mode transition requested."));
        }

        [XRMcp(Name = "select_node_by_name")]
        [Description("Select scene nodes by display name.")]
        public static Task<McpToolResponse> SelectNodeByNameAsync(
            McpToolContext context,
            [McpName("node_name"), Description("Node display name to match.")] string nodeName,
            [McpName("scene_name"), Description("Optional scene name or ID.")] string? sceneName = null,
            [McpName("match_mode"), Description("Match mode: exact or contains.")] string matchMode = "contains",
            [McpName("append"), Description("Append matched nodes to existing selection.")] bool append = false)
        {
            if (string.IsNullOrWhiteSpace(nodeName))
                return Task.FromResult(new McpToolResponse("node_name must be provided.", isError: true));

            var world = context.WorldInstance;
            var scene = ResolveScene(world, sceneName);
            if (scene is null)
                return Task.FromResult(new McpToolResponse("No active scene found.", isError: true));

            bool exact = string.Equals(matchMode, "exact", StringComparison.OrdinalIgnoreCase);
            var matches = scene.RootNodes
                .Where(root => root is not null)
                .SelectMany(root => EnumerateHierarchy(root!))
                .Where(node => exact
                    ? string.Equals(node.Name, nodeName, StringComparison.OrdinalIgnoreCase)
                    : (node.Name?.Contains(nodeName, StringComparison.OrdinalIgnoreCase) ?? false))
                .Distinct()
                .ToArray();

            if (matches.Length == 0)
                return Task.FromResult(new McpToolResponse($"No nodes matched '{nodeName}'.", isError: true));

            if (append && Selection.SceneNodes.Length > 0)
                Selection.SceneNodes = [.. Selection.SceneNodes, .. matches.Distinct()];
            else
                Selection.SceneNodes = matches;

            return Task.FromResult(new McpToolResponse($"Selected {matches.Length} node(s) by name.", new
            {
                selected = matches.Select(node => new { id = node.ID, name = node.Name, path = BuildNodePath(node) }).ToArray()
            }));
        }

        [XRMcp(Name = "create_primitive_shape")]
        [Description("Create a visible primitive shape node with a default material in the active scene.")]
        public static Task<McpToolResponse> CreatePrimitiveShapeAsync(
            McpToolContext context,
            [McpName("shape_type"), Description("Primitive type (cube/box, sphere, cone).")]
            string shapeType,
            [McpName("name"), Description("Optional node name.")] string? name = null,
            [McpName("parent_id"), Description("Optional parent node ID.")] string? parentId = null,
            [McpName("scene_name"), Description("Optional scene name; defaults to first active scene.")] string? sceneName = null,
            [McpName("size"), Description("Uniform size scale for the primitive.")] float size = 1.0f,
            [McpName("color"), Description("Optional color for the default material, e.g. {R:1,G:0,B:0,A:1} or hex '#FF0000'. Defaults to a neutral gray.")] object? color = null)
        {
            var world = context.WorldInstance;
            var scene = ResolveScene(world, sceneName);
            if (scene is null)
                return Task.FromResult(new McpToolResponse("No active scene found.", isError: true));

            size = Math.Max(0.001f, size);

            // Resolve color for the default material.
            ColorF4 matColor = new(0.6f, 0.6f, 0.6f, 1f);
            if (color is not null)
            {
                if (McpToolRegistry.TryConvertValue(color, typeof(ColorF4), out var converted, out _) && converted is ColorF4 c)
                    matColor = c;
            }

            // Create a default lit material so the primitive is immediately visible.
            var defaultMaterial = XRMaterial.CreateLitColorMaterial(matColor, deferred: true);

            SceneNode node;
            if (!string.IsNullOrWhiteSpace(parentId))
            {
                if (!TryGetNodeById(world, parentId!, out var parent, out var parentError) || parent is null)
                    return Task.FromResult(new McpToolResponse(parentError ?? "Parent node not found.", isError: true));

                node = parent.NewChild(name ?? "Primitive");
            }
            else
            {
                node = new SceneNode(name ?? "Primitive");
                scene.RootNodes.Add(node);
                if (scene.IsVisible)
                    world.RootNodes.Add(node);
            }

            string normalized = NormalizePrimitiveShapeType(shapeType);
            switch (normalized)
            {
                case "cube":
                case "box":
                    var box = node.AddComponent<BoxMeshComponent>();
                    if (box is null)
                        return Task.FromResult(new McpToolResponse("Failed to add BoxMeshComponent.", isError: true));
                    box.Material = defaultMaterial;
                    float half = size * 0.5f;
                    box.Box = new AABB(new Vector3(-half), new Vector3(half));
                    node.Name = name ?? "Box";
                    break;

                case "sphere":
                    var sphere = node.AddComponent<SphereMeshComponent>();
                    if (sphere is null)
                        return Task.FromResult(new McpToolResponse("Failed to add SphereMeshComponent.", isError: true));
                    sphere.Material = defaultMaterial;
                    sphere.Radius = size * 0.5f;
                    sphere.Shape = new Sphere(Vector3.Zero, sphere.Radius);
                    node.Name = name ?? "Sphere";
                    break;

                case "cone":
                    var cone = node.AddComponent<ConeMeshComponent>();
                    if (cone is null)
                        return Task.FromResult(new McpToolResponse("Failed to add ConeMeshComponent.", isError: true));
                    cone.Material = defaultMaterial;
                    cone.Radius = size * 0.5f;
                    cone.Height = size;
                    cone.Shape = new Cone(Vector3.Zero, Globals.Up, cone.Height, cone.Radius);
                    node.Name = name ?? "Cone";
                    break;

                default:
                    node.Destroy();
                    return Task.FromResult(new McpToolResponse($"Unsupported shape_type '{shapeType}'. Supported: cube, box, sphere, cone.", isError: true));
            }

            // Record structural undo for primitive creation
            var parentTfm = node.Transform.Parent;
            using var interaction = Undo.BeginUserInteraction();
            using var changeScope = Undo.BeginChange("MCP Create Primitive");
            Undo.TrackSceneNode(node);
            Undo.RecordStructuralChange("Create Primitive",
                undoAction: () =>
                {
                    if (parentTfm is not null)
                        parentTfm.RemoveChild(node.Transform, EParentAssignmentMode.Immediate);
                    else
                        world.RootNodes.Remove(node);
                    node.IsActiveSelf = false;
                },
                redoAction: () =>
                {
                    if (parentTfm is not null)
                        node.Transform.SetParent(parentTfm, false, EParentAssignmentMode.Immediate);
                    else
                        world.RootNodes.Add(node);
                    node.IsActiveSelf = true;
                    Undo.TrackSceneNode(node);
                });

            Selection.SceneNode = node;
            return Task.FromResult(new McpToolResponse($"Created {normalized} primitive.", new { id = node.ID, path = BuildNodePath(node) }));
        }

        [XRMcp(Name = "bake_shape_components_to_model", Permission = McpPermissionLevel.Mutate, PermissionReason = "Creates or updates a model component from shape components.")]
        [Description("Bake ShapeMeshComponent nodes into one ModelComponent using boolean ops (union/intersect/difference/xor).")]
        public static Task<McpToolResponse> BakeShapeComponentsToModelAsync(
            McpToolContext context,
            [McpName("node_ids"), Description("Optional source node IDs. If omitted, current selection is used.")] string[]? nodeIds = null,
            [McpName("operation"), Description("Boolean operation: union, intersect, difference, xor.")] string operation = "union",
            [McpName("target_node_id"), Description("Optional target node to receive/update ModelComponent. If omitted, a new node is created.")] string? targetNodeId = null,
            [McpName("result_name"), Description("Optional name for the created/updated target node.")] string? resultName = null)
        {
            if (!TryParseBooleanOperation(operation, out var op))
            {
                return Task.FromResult(new McpToolResponse(
                    $"Unsupported operation '{operation}'. Supported: union, intersect, difference, xor.",
                    isError: true));
            }

            var world = context.WorldInstance;

            List<SceneNode> sourceNodes = [];
            if (nodeIds is { Length: > 0 })
            {
                foreach (string id in nodeIds.Where(id => !string.IsNullOrWhiteSpace(id)))
                {
                    if (!TryGetNodeById(world, id, out var node, out var error) || node is null)
                        return Task.FromResult(new McpToolResponse(error ?? $"Scene node '{id}' not found.", isError: true));
                    sourceNodes.Add(node);
                }
            }
            else
            {
                sourceNodes.AddRange(Selection.SceneNodes.Where(node => node.World == world));
            }

            sourceNodes = [.. sourceNodes.Distinct()];
            if (sourceNodes.Count == 0)
                return Task.FromResult(new McpToolResponse("No source nodes provided. Pass node_ids or select nodes first.", isError: true));

            List<(XRMesh Mesh, Matrix4x4? Transform)> meshes = [];
            XRMaterial? outputMaterial = null;
            List<object> included = [];

            foreach (SceneNode sourceNode in sourceNodes)
            {
                ShapeMeshComponent[] shapeComponents = sourceNode.Components.OfType<ShapeMeshComponent>().ToArray();
                if (shapeComponents.Length == 0)
                    continue;

                foreach (ShapeMeshComponent shape in shapeComponents)
                {
                    if (shape.Shape is null)
                        continue;

                    XRMesh? mesh = XRMesh.Shapes.FromVolume(shape.Shape, wireframe: false);
                    if (mesh is null)
                        continue;

                    outputMaterial ??= shape.Material;
                    meshes.Add((mesh, sourceNode.Transform.WorldMatrix));
                    included.Add(new
                    {
                        id = sourceNode.ID,
                        name = sourceNode.Name,
                        path = BuildNodePath(sourceNode),
                        componentId = shape.ID,
                        componentType = shape.GetType().Name
                    });
                }
            }

            if (meshes.Count == 0)
            {
                return Task.FromResult(new McpToolResponse(
                    "No valid ShapeMeshComponent sources found. Ensure source nodes have a shape component with a non-null Shape.",
                    isError: true));
            }

            XRMesh bakedMesh = XRMeshBooleanOperations.BakeShapes(meshes, op);
            if (bakedMesh.VertexCount == 0)
                return Task.FromResult(new McpToolResponse("Boolean bake produced an empty mesh.", isError: true));

            SceneNode targetNode;
            bool createdNode = false;
            if (!string.IsNullOrWhiteSpace(targetNodeId))
            {
                if (!TryGetNodeById(world, targetNodeId!, out var existing, out var error) || existing is null)
                    return Task.FromResult(new McpToolResponse(error ?? "target_node_id not found.", isError: true));
                targetNode = existing;
            }
            else
            {
                SceneNode sourceForScene = sourceNodes[0];
                XRScene? scene = FindSceneForNode(sourceForScene, world) ?? ResolveScene(world, null);
                if (scene is null)
                    return Task.FromResult(new McpToolResponse("No active scene found to place baked model node.", isError: true));

                targetNode = new SceneNode(resultName ?? $"Baked_{op}");
                scene.RootNodes.Add(targetNode);
                if (scene.IsVisible)
                    world.RootNodes.Add(targetNode);
                createdNode = true;
            }

            if (!string.IsNullOrWhiteSpace(resultName))
                targetNode.Name = resultName;

            ModelComponent modelComponent = targetNode.GetComponent<ModelComponent>() ?? targetNode.AddComponent<ModelComponent>()!;
            if (modelComponent is null)
                return Task.FromResult(new McpToolResponse("Failed to add or resolve ModelComponent on target node.", isError: true));

            outputMaterial ??= XRMaterial.CreateLitColorMaterial(new ColorF4(0.7f, 0.7f, 0.7f, 1f), deferred: true);
            Model model = new(new SubMesh(bakedMesh, outputMaterial));
            modelComponent.Model = model;

            Selection.SceneNode = targetNode;

            return Task.FromResult(new McpToolResponse("Baked shape components into a singular model component.", new
            {
                targetNodeId = targetNode.ID,
                targetNodeName = targetNode.Name,
                operation = op.ToString(),
                sourceCount = meshes.Count,
                createdNode,
                vertexCount = bakedMesh.VertexCount,
                includedSources = included.ToArray()
            }));
        }

        [XRMcp(Name = "save_world", Permission = McpPermissionLevel.Destructive, PermissionReason = "Writes world data to the file system.")]
        [Description("Save the active world asset to disk.")]
        public static Task<McpToolResponse> SaveWorldAsync(
            McpToolContext context,
            [McpName("output_dir"), Description("Optional target directory.")] string? outputDir = null)
        {
            var world = context.WorldInstance.TargetWorld;
            if (world is null)
                return Task.FromResult(new McpToolResponse("No active world found.", isError: true));

            var assets = Engine.Assets;
            if (assets is null)
                return Task.FromResult(new McpToolResponse("Asset manager is unavailable.", isError: true));

            string directory = string.IsNullOrWhiteSpace(outputDir)
                ? assets.GameAssetsPath
                : outputDir!;

            Directory.CreateDirectory(directory);
            assets.SaveTo(world, directory);

            return Task.FromResult(new McpToolResponse("Saved active world.", new
            {
                id = world.ID,
                name = world.Name,
                filePath = world.FilePath,
                outputDir = directory
            }));
        }

        [XRMcp(Name = "load_world", Permission = McpPermissionLevel.Destructive, PermissionReason = "Replaces the active world, discarding unsaved changes.")]
        [Description("Load a world asset and set it as active on the current world instance.")]
        public static Task<McpToolResponse> LoadWorldAsync(
            McpToolContext context,
            [McpName("asset_path"), Description("Path to world asset file.")] string? assetPath = null,
            [McpName("world_name"), Description("Name of an already-loaded world asset.")] string? worldName = null)
        {
            var assets = Engine.Assets;
            if (assets is null)
                return Task.FromResult(new McpToolResponse("Asset manager is unavailable.", isError: true));

            XRWorld? world = null;

            if (!string.IsNullOrWhiteSpace(assetPath))
            {
                world = assets.Load<XRWorld>(assetPath!);
            }
            else if (!string.IsNullOrWhiteSpace(worldName))
            {
                world = assets.LoadedAssetsByIDInternal.Values
                    .OfType<XRWorld>()
                    .FirstOrDefault(candidate => string.Equals(candidate.Name, worldName, StringComparison.OrdinalIgnoreCase));
            }

            if (world is null)
                return Task.FromResult(new McpToolResponse("World asset not found. Provide asset_path or the name of an already-loaded world.", isError: true));

            context.WorldInstance.TargetWorld = world;
            Selection.Clear();

            return Task.FromResult(new McpToolResponse("Loaded world.", new
            {
                id = world.ID,
                name = world.Name,
                sceneCount = world.Scenes.Count,
                filePath = world.FilePath
            }));
        }

        [XRMcp(Name = "list_tools")]
        [Description("List all MCP tools currently registered by the editor.")]
        public static Task<McpToolResponse> ListToolsAsync(McpToolContext context)
        {
            var tools = McpToolRegistry.Tools
                .Select(tool => new
                {
                    name = tool.Name,
                    description = tool.Description
                })
                .OrderBy(tool => tool.name)
                .ToArray();

            return Task.FromResult(new McpToolResponse($"Listed {tools.Length} tools.", new { tools }));
        }

        private static string NormalizePrimitiveShapeType(string input)
            => (input ?? string.Empty).Trim().ToLowerInvariant();

        private static bool TryParseBooleanOperation(string? operation, out EBooleanOperation result)
        {
            switch ((operation ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "union":
                case "or":
                case "add":
                    result = EBooleanOperation.Union;
                    return true;
                case "intersect":
                case "intersection":
                case "and":
                    result = EBooleanOperation.Intersect;
                    return true;
                case "difference":
                case "subtract":
                case "sub":
                    result = EBooleanOperation.Difference;
                    return true;
                case "xor":
                case "symmetric_difference":
                case "symmetricdifference":
                    result = EBooleanOperation.SymmetricDifference;
                    return true;
                default:
                    result = EBooleanOperation.Union;
                    return false;
            }
        }
    }
}
