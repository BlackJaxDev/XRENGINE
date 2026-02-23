using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using XREngine;
using XREngine.Components.Mesh.Shapes;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Editor;
using XREngine.Scene;

namespace XREngine.Editor.Mcp
{
    public sealed partial class EditorMcpActions
    {
        [XRMcp]
        [McpName("undo")]
        [Description("Undo the most recent editor change.")]
        public static Task<McpToolResponse> UndoAsync(McpToolContext context)
        {
            bool success = Undo.TryUndo();
            return Task.FromResult(success
                ? new McpToolResponse("Undo applied.")
                : new McpToolResponse("Nothing to undo.", isError: true));
        }

        [XRMcp]
        [McpName("redo")]
        [Description("Redo the most recently undone editor change.")]
        public static Task<McpToolResponse> RedoAsync(McpToolContext context)
        {
            bool success = Undo.TryRedo();
            return Task.FromResult(success
                ? new McpToolResponse("Redo applied.")
                : new McpToolResponse("Nothing to redo.", isError: true));
        }

        [XRMcp]
        [McpName("clear_selection")]
        [Description("Clear the current scene-node selection.")]
        public static Task<McpToolResponse> ClearSelectionAsync(McpToolContext context)
        {
            Selection.Clear();
            return Task.FromResult(new McpToolResponse("Selection cleared."));
        }

        [XRMcp]
        [McpName("delete_selected_nodes")]
        [Description("Delete all currently selected scene nodes.")]
        public static Task<McpToolResponse> DeleteSelectedNodesAsync(McpToolContext context)
        {
            var targets = Selection.SceneNodes
                .Where(node => node.World == context.WorldInstance)
                .Distinct()
                .ToArray();

            if (targets.Length == 0)
                return Task.FromResult(new McpToolResponse("No selected nodes found in the active world.", isError: true));

            foreach (var node in targets)
                node.Destroy();

            Selection.Clear();
            return Task.FromResult(new McpToolResponse($"Deleted {targets.Length} selected node(s)."));
        }

        [XRMcp]
        [McpName("enter_play_mode")]
        [Description("Enter play mode.")]
        public static Task<McpToolResponse> EnterPlayModeAsync(McpToolContext context)
        {
            if (EditorState.InPlayMode)
                return Task.FromResult(new McpToolResponse("Already in play mode."));

            EditorState.EnterPlayMode();
            return Task.FromResult(new McpToolResponse("Play mode transition requested."));
        }

        [XRMcp]
        [McpName("exit_play_mode")]
        [Description("Exit play mode.")]
        public static Task<McpToolResponse> ExitPlayModeAsync(McpToolContext context)
        {
            if (EditorState.InEditMode)
                return Task.FromResult(new McpToolResponse("Already in edit mode."));

            EditorState.ExitPlayMode();
            return Task.FromResult(new McpToolResponse("Exit play mode transition requested."));
        }

        [XRMcp]
        [McpName("select_node_by_name")]
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

        [XRMcp]
        [McpName("create_primitive_shape")]
        [Description("Create a primitive shape node in the active scene.")]
        public static Task<McpToolResponse> CreatePrimitiveShapeAsync(
            McpToolContext context,
            [McpName("shape_type"), Description("Primitive type (cube/box, sphere, cone).")]
            string shapeType,
            [McpName("name"), Description("Optional node name.")] string? name = null,
            [McpName("parent_id"), Description("Optional parent node ID.")] string? parentId = null,
            [McpName("scene_name"), Description("Optional scene name; defaults to first active scene.")] string? sceneName = null,
            [McpName("size"), Description("Uniform size scale for the primitive.")] float size = 1.0f)
        {
            var world = context.WorldInstance;
            var scene = ResolveScene(world, sceneName);
            if (scene is null)
                return Task.FromResult(new McpToolResponse("No active scene found.", isError: true));

            size = Math.Max(0.001f, size);

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
                    float half = size * 0.5f;
                    box.Box = new AABB(new Vector3(-half), new Vector3(half));
                    node.Name = name ?? "Box";
                    break;

                case "sphere":
                    var sphere = node.AddComponent<SphereMeshComponent>();
                    if (sphere is null)
                        return Task.FromResult(new McpToolResponse("Failed to add SphereMeshComponent.", isError: true));
                    sphere.Radius = size * 0.5f;
                    sphere.Shape = new Sphere(Vector3.Zero, sphere.Radius);
                    node.Name = name ?? "Sphere";
                    break;

                case "cone":
                    var cone = node.AddComponent<ConeMeshComponent>();
                    if (cone is null)
                        return Task.FromResult(new McpToolResponse("Failed to add ConeMeshComponent.", isError: true));
                    cone.Radius = size * 0.5f;
                    cone.Height = size;
                    cone.Shape = new Cone(Vector3.Zero, Globals.Up, cone.Height, cone.Radius);
                    node.Name = name ?? "Cone";
                    break;

                default:
                    node.Destroy();
                    return Task.FromResult(new McpToolResponse($"Unsupported shape_type '{shapeType}'. Supported: cube, box, sphere, cone.", isError: true));
            }

            Selection.SceneNode = node;
            return Task.FromResult(new McpToolResponse($"Created {normalized} primitive.", new { id = node.ID, path = BuildNodePath(node) }));
        }

        [XRMcp]
        [McpName("save_world")]
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

        [XRMcp]
        [McpName("load_world")]
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

        [XRMcp]
        [McpName("list_tools")]
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
    }
}
