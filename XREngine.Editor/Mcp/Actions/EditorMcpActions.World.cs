using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using XREngine.Scene;
using XREngine.Data.Core;

namespace XREngine.Editor.Mcp
{
    public sealed partial class EditorMcpActions
    {
        /// <summary>
        /// Lists all active world instances and their associated scenes.
        /// </summary>
        /// <param name="context">The MCP tool execution context.</param>
        /// <returns>
        /// A response containing an array of worlds, each with:
        /// <list type="bullet">
        /// <item><description><c>id</c> - The world's unique GUID</description></item>
        /// <item><description><c>name</c> - The world's display name</description></item>
        /// <item><description><c>scenes</c> - Array of scenes with id and name</description></item>
        /// <item><description><c>playState</c> - Current play state (e.g., "Playing", "Stopped")</description></item>
        /// </list>
        /// </returns>
        [XRMcp]
        [McpName("list_worlds")]
        [Description("List active world instances and their scenes.")]
        public static Task<McpToolResponse> ListWorldsAsync(
            McpToolContext context)
        {
            var worlds = Engine.WorldInstances.Select(instance => new
            {
                id = instance.TargetWorld?.ID,
                name = instance.TargetWorld?.Name ?? "<unnamed>",
                scenes = instance.TargetWorld?.Scenes.Select(scene => new { id = scene.ID, name = scene.Name ?? "<unnamed>" }).ToArray()
                    ?? Array.Empty<object>(),
                playState = instance.PlayState.ToString()
            }).ToArray();

            return Task.FromResult(new McpToolResponse("Listed active world instances.", new { worlds }));
        }

        /// <summary>
        /// Lists all scenes in the active world.
        /// </summary>
        [XRMcp]
        [McpName("list_scenes")]
        [Description("List scenes in the active world.")]
        public static Task<McpToolResponse> ListScenesAsync(
            McpToolContext context)
        {
            var world = context.WorldInstance;
            var targetWorld = world.TargetWorld;
            if (targetWorld is null)
                return Task.FromResult(new McpToolResponse("No active world found.", isError: true));

            var scenes = targetWorld.Scenes.Select(scene => new
            {
                id = scene.ID,
                name = scene.Name ?? "<unnamed>",
                isVisible = scene.IsVisible,
                rootCount = scene.RootNodes.Count
            }).ToArray();

            return Task.FromResult(new McpToolResponse("Listed scenes.", new { scenes }));
        }

        /// <summary>
        /// Creates a new scene in the active world.
        /// </summary>
        [XRMcp]
        [McpName("create_scene")]
        [Description("Create a new scene in the active world.")]
        public static Task<McpToolResponse> CreateSceneAsync(
            McpToolContext context,
            [McpName("name"), Description("Scene name.")] string name,
            [McpName("is_visible"), Description("Whether the scene is visible.")] bool isVisible = true)
        {
            var world = context.WorldInstance;
            var targetWorld = world.TargetWorld;
            if (targetWorld is null)
                return Task.FromResult(new McpToolResponse("No active world found.", isError: true));

            var scene = new XRScene(name) { IsVisible = isVisible };
            targetWorld.Scenes.Add(scene);
            world.LoadScene(scene);

            return Task.FromResult(new McpToolResponse($"Created scene '{name}'.", new { id = scene.ID }));
        }

        /// <summary>
        /// Deletes a scene from the active world.
        /// </summary>
        [XRMcp]
        [McpName("delete_scene")]
        [Description("Delete a scene from the active world.")]
        public static Task<McpToolResponse> DeleteSceneAsync(
            McpToolContext context,
            [McpName("scene_name"), Description("Scene name or ID to delete.")] string sceneName)
        {
            var world = context.WorldInstance;
            if (!TryResolveScene(world, sceneName, out var scene, out var error) || scene is null)
                return Task.FromResult(new McpToolResponse(error ?? "Scene not found.", isError: true));

            world.UnloadScene(scene);
            world.TargetWorld?.Scenes.Remove(scene);
            return Task.FromResult(new McpToolResponse($"Deleted scene '{scene.Name ?? scene.ID.ToString()}'."));
        }

        /// <summary>
        /// Toggles scene visibility in the active world.
        /// </summary>
        [XRMcp]
        [McpName("toggle_scene_visibility")]
        [Description("Toggle scene visibility.")]
        public static Task<McpToolResponse> ToggleSceneVisibilityAsync(
            McpToolContext context,
            [McpName("scene_name"), Description("Scene name or ID.")] string sceneName,
            [McpName("is_visible"), Description("Whether the scene is visible.")] bool isVisible)
        {
            var world = context.WorldInstance;
            if (!TryResolveScene(world, sceneName, out var scene, out var error) || scene is null)
                return Task.FromResult(new McpToolResponse(error ?? "Scene not found.", isError: true));

            scene.IsVisible = isVisible;
            if (isVisible)
                world.LoadScene(scene);
            else
                world.UnloadScene(scene);

            return Task.FromResult(new McpToolResponse($"Scene '{scene.Name ?? scene.ID.ToString()}' visibility set to {isVisible}."));
        }

        /// <summary>
        /// Sets the active scene by moving it to the front of the scene list and ensuring it is visible.
        /// </summary>
        [XRMcp]
        [McpName("set_active_scene")]
        [Description("Set a scene as active (first in scene list).")]
        public static Task<McpToolResponse> SetActiveSceneAsync(
            McpToolContext context,
            [McpName("scene_name"), Description("Scene name or ID.")] string sceneName)
        {
            var world = context.WorldInstance;
            var targetWorld = world.TargetWorld;
            if (targetWorld is null)
                return Task.FromResult(new McpToolResponse("No active world found.", isError: true));

            if (!TryResolveScene(world, sceneName, out var scene, out var error) || scene is null)
                return Task.FromResult(new McpToolResponse(error ?? "Scene not found.", isError: true));

            targetWorld.Scenes.Remove(scene);
            targetWorld.Scenes.Insert(0, scene);
            if (!scene.IsVisible)
            {
                scene.IsVisible = true;
                world.LoadScene(scene);
            }

            return Task.FromResult(new McpToolResponse($"Set '{scene.Name ?? scene.ID.ToString()}' as the active scene."));
        }

        /// <summary>
        /// Exports a scene asset to a directory.
        /// </summary>
        [XRMcp]
        [McpName("export_scene")]
        [Description("Export a scene asset to a directory.")]
        public static Task<McpToolResponse> ExportSceneAsync(
            McpToolContext context,
            [McpName("scene_name"), Description("Scene name or ID to export.")] string sceneName,
            [McpName("output_dir"), Description("Target directory for the exported asset.")] string outputDir)
        {
            var world = context.WorldInstance;
            if (!TryResolveScene(world, sceneName, out var scene, out var error) || scene is null)
                return Task.FromResult(new McpToolResponse(error ?? "Scene not found.", isError: true));

            var assets = Engine.Assets;
            if (assets is null)
                return Task.FromResult(new McpToolResponse("Asset manager is unavailable.", isError: true));

            if (string.IsNullOrWhiteSpace(outputDir))
                return Task.FromResult(new McpToolResponse("Output directory must be provided.", isError: true));

            Directory.CreateDirectory(outputDir);
            assets.SaveTo(scene, outputDir);
            return Task.FromResult(new McpToolResponse($"Exported scene '{scene.Name ?? scene.ID.ToString()}'.", new { path = scene.FilePath }));
        }

        /// <summary>
        /// Imports a scene asset from disk and adds it to the active world.
        /// </summary>
        [XRMcp]
        [McpName("import_scene")]
        [Description("Import a scene asset from disk and add it to the active world.")]
        public static Task<McpToolResponse> ImportSceneAsync(
            McpToolContext context,
            [McpName("asset_path"), Description("Scene asset path to load.")] string assetPath,
            [McpName("force_visible"), Description("Optional visibility override.")] bool? forceVisible = null)
        {
            var world = context.WorldInstance;
            var targetWorld = world.TargetWorld;
            if (targetWorld is null)
                return Task.FromResult(new McpToolResponse("No active world found.", isError: true));

            var assets = Engine.Assets;
            if (assets is null)
                return Task.FromResult(new McpToolResponse("Asset manager is unavailable.", isError: true));

            if (string.IsNullOrWhiteSpace(assetPath))
                return Task.FromResult(new McpToolResponse("Asset path must be provided.", isError: true));

            var scene = assets.Load<XRScene>(assetPath);
            if (scene is null)
                return Task.FromResult(new McpToolResponse("Failed to load scene asset.", isError: true));

            if (forceVisible.HasValue)
                scene.IsVisible = forceVisible.Value;

            if (!targetWorld.Scenes.Contains(scene))
                targetWorld.Scenes.Add(scene);

            world.LoadScene(scene);
            return Task.FromResult(new McpToolResponse($"Imported scene '{scene.Name ?? scene.ID.ToString()}'.", new { id = scene.ID }));
        }

        /// <summary>
        /// Validates a scene for common issues (null roots, missing world references, duplicates).
        /// </summary>
        [XRMcp]
        [McpName("validate_scene")]
        [Description("Validate a scene for common hierarchy issues.")]
        public static Task<McpToolResponse> ValidateSceneAsync(
            McpToolContext context,
            [McpName("scene_name"), Description("Scene name or ID to validate.")] string? sceneName = null)
        {
            var world = context.WorldInstance;
            if (!TryResolveScene(world, sceneName, out var scene, out var error) || scene is null)
                return Task.FromResult(new McpToolResponse(error ?? "Scene not found.", isError: true));

            var issues = new System.Collections.Generic.List<string>();

            if (scene.RootNodes.Any(root => root is null))
                issues.Add("Scene contains null root nodes.");

            foreach (var root in scene.RootNodes)
            {
                if (root is null)
                    continue;

                if (root.Parent is not null)
                    issues.Add($"Root node '{root.Name ?? root.ID.ToString()}' has a parent.");

                if (scene.IsVisible && root.World != world)
                    issues.Add($"Root node '{root.Name ?? root.ID.ToString()}' is not bound to the active world.");
            }

            return Task.FromResult(new McpToolResponse(issues.Count == 0
                ? "Scene validation passed."
                : $"Scene validation found {issues.Count} issue(s).", new { issues }));
        }
    }
}
