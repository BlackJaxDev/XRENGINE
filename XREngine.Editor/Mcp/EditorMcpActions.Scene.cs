using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using XREngine;
using XREngine.Components.Scene.Transforms;
using XREngine.Data.Core;
using XREngine.Data.Transforms.Rotations;
using XREngine.Editor;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.Editor.Mcp
{
    public sealed partial class EditorMcpActions
    {
        /// <summary>
        /// Lists all scene nodes in the active world/scene hierarchy.
        /// </summary>
        /// <param name="context">The MCP tool execution context.</param>
        /// <param name="sceneName">Optional scene name to query; defaults to the first active scene.</param>
        /// <param name="maxDepth">Optional maximum depth to traverse in the hierarchy.</param>
        /// <returns>
        /// A response containing an array of nodes, each with:
        /// <list type="bullet">
        /// <item><description><c>id</c> - The unique GUID of the node</description></item>
        /// <item><description><c>name</c> - The display name of the node</description></item>
        /// <item><description><c>path</c> - The full hierarchy path (e.g., "Root/Parent/Child")</description></item>
        /// <item><description><c>depth</c> - The depth level in the hierarchy</description></item>
        /// <item><description><c>childCount</c> - Number of direct children</description></item>
        /// <item><description><c>isActive</c> - Whether the node is active</description></item>
        /// </list>
        /// </returns>
        [XRMcp]
        [McpName("list_scene_nodes")]
        [Description("List scene nodes in the active world/scene.")]
        public static Task<McpToolResponse> ListSceneNodesAsync(
            McpToolContext context,
            [McpName("scene_name"), Description("Optional scene name; defaults to the first active scene.")]
            string? sceneName = null,
            [McpName("max_depth"), Description("Optional max depth to traverse.")] int? maxDepth = null)
        {
            var scene = ResolveScene(context.WorldInstance, sceneName);
            if (scene is null)
                return Task.FromResult(new McpToolResponse("No active scene found.", isError: true));

            var nodes = new List<object>();
            foreach (var root in scene.RootNodes)
            {
                if (root is null) continue;
                CollectNodes(root, nodes, 0, maxDepth);
            }

            return Task.FromResult(new McpToolResponse("Listed scene nodes.", new { nodes }));
        }

        /// <summary>
        /// Retrieves detailed information about a specific scene node, including its transform and attached components.
        /// </summary>
        /// <param name="context">The MCP tool execution context.</param>
        /// <param name="nodeId">The GUID of the scene node to inspect.</param>
        /// <returns>
        /// A response containing:
        /// <list type="bullet">
        /// <item><description><c>id</c> - The node's unique GUID</description></item>
        /// <item><description><c>name</c> - The node's display name</description></item>
        /// <item><description><c>path</c> - Full hierarchy path</description></item>
        /// <item><description><c>parentId</c> - Parent node's GUID (null if root)</description></item>
        /// <item><description><c>isActive</c> - Whether the node is active</description></item>
        /// <item><description><c>components</c> - Array of attached components with id, name, and type</description></item>
        /// <item><description><c>transform</c> - Translation, rotation (pitch/yaw/roll in degrees), and scale</description></item>
        /// </list>
        /// </returns>
        [XRMcp]
        [McpName("get_scene_node_info")]
        [Description("Get detailed info about a scene node, including transform and components.")]
        public static Task<McpToolResponse> GetSceneNodeInfoAsync(
            McpToolContext context,
            [McpName("node_id"), Description("Scene node ID to inspect.")] string nodeId)
        {
            if (!TryGetNodeById(context.WorldInstance, nodeId, out var node, out var error) || node is null)
                return Task.FromResult(new McpToolResponse(error ?? "Scene node not found.", isError: true));

            var transform = node.Transform as Transform;
            var rotator = transform is null ? default : Rotator.FromQuaternion(transform.Rotation);

            var info = new
            {
                id = node.ID,
                name = node.Name,
                path = BuildNodePath(node),
                parentId = node.Parent?.ID,
                isActive = node.IsActiveSelf,
                components = node.Components.Select(comp => new
                {
                    id = comp.ID,
                    name = comp.Name,
                    type = comp.GetType().FullName ?? comp.GetType().Name
                }).ToArray(),
                transform = transform is null
                    ? null
                    : new
                    {
                        translation = transform.Translation,
                        rotation = new { pitch = rotator.Pitch, yaw = rotator.Yaw, roll = rotator.Roll },
                        scale = transform.Scale
                    }
            };

            return Task.FromResult(new McpToolResponse($"Retrieved info for '{nodeId}'.", info));
        }

        /// <summary>
        /// Sets whether a scene node is active in the hierarchy.
        /// Inactive nodes (and their children) are not rendered or updated.
        /// </summary>
        /// <param name="context">The MCP tool execution context.</param>
        /// <param name="nodeId">The GUID of the scene node to update.</param>
        /// <param name="isActive">True to activate the node, false to deactivate.</param>
        /// <returns>A confirmation message indicating the new active state.</returns>
        [XRMcp]
        [McpName("set_node_active")]
        [Description("Set whether a scene node is active in the hierarchy.")]
        public static Task<McpToolResponse> SetNodeActiveAsync(
            McpToolContext context,
            [McpName("node_id"), Description("Scene node ID to update.")] string nodeId,
            [McpName("is_active"), Description("Whether the node is active.")] bool isActive)
        {
            if (!TryGetNodeById(context.WorldInstance, nodeId, out var node, out var error) || node is null)
                return Task.FromResult(new McpToolResponse(error ?? "Scene node not found.", isError: true));

            node.IsActiveSelf = isActive;
            return Task.FromResult(new McpToolResponse($"Set '{nodeId}' active={isActive}."));
        }

        /// <summary>
        /// Reparents a scene node to a new parent, or moves it to the root level.
        /// </summary>
        /// <param name="context">The MCP tool execution context.</param>
        /// <param name="nodeId">The GUID of the scene node to reparent.</param>
        /// <param name="newParentId">The GUID of the new parent node. Omit or pass null to move to root.</param>
        /// <param name="preserveWorldTransform">If true, the node's world-space transform is preserved during reparenting.</param>
        /// <returns>A confirmation message indicating the new parent.</returns>
        [XRMcp]
        [McpName("reparent_node")]
        [Description("Reparent a scene node to a new parent.")]
        public static Task<McpToolResponse> ReparentNodeAsync(
            McpToolContext context,
            [McpName("node_id"), Description("Scene node ID to reparent.")] string nodeId,
            [McpName("new_parent_id"), Description("New parent node ID (omit to unparent).")]
            string? newParentId = null,
            [McpName("preserve_world_transform"), Description("Preserve world transform when reparenting.")] bool preserveWorldTransform = false)
        {
            if (!TryGetNodeById(context.WorldInstance, nodeId, out var node, out var error) || node is null)
                return Task.FromResult(new McpToolResponse(error ?? "Scene node not found.", isError: true));

            SceneNode? newParent = null;
            if (!string.IsNullOrWhiteSpace(newParentId))
            {
                if (!TryGetNodeById(context.WorldInstance, newParentId!, out newParent, out var parentError) || newParent is null)
                    return Task.FromResult(new McpToolResponse(parentError ?? "Parent node not found.", isError: true));
            }

            node.Transform.SetParent(newParent?.Transform, preserveWorldTransform, EParentAssignmentMode.Immediate);
            return Task.FromResult(new McpToolResponse($"Reparented '{nodeId}' to '{newParentId ?? "<root>"}'."));
        }

        /// <summary>
        /// Deletes a scene node and all of its children from the scene.
        /// This operation is irreversible.
        /// </summary>
        /// <param name="context">The MCP tool execution context.</param>
        /// <param name="nodeId">The GUID of the scene node to delete.</param>
        /// <returns>A confirmation message indicating the node was deleted.</returns>
        [XRMcp]
        [McpName("delete_scene_node")]
        [Description("Delete a scene node and its hierarchy.")]
        public static Task<McpToolResponse> DeleteSceneNodeAsync(
            McpToolContext context,
            [McpName("node_id"), Description("Scene node ID to delete.")] string nodeId)
        {
            if (!TryGetNodeById(context.WorldInstance, nodeId, out var node, out var error) || node is null)
                return Task.FromResult(new McpToolResponse(error ?? "Scene node not found.", isError: true));

            node.Destroy();
            return Task.FromResult(new McpToolResponse($"Deleted scene node '{nodeId}'."));
        }

        /// <summary>
        /// Creates a new scene node in the active world/scene.
        /// </summary>
        /// <param name="context">The MCP tool execution context.</param>
        /// <param name="name">The display name for the new scene node.</param>
        /// <param name="parentId">Optional GUID of the parent node. If omitted, the node is created at root level.</param>
        /// <param name="sceneName">Optional scene name; defaults to the first active scene.</param>
        /// <returns>
        /// A response containing:
        /// <list type="bullet">
        /// <item><description><c>id</c> - The GUID of the newly created node</description></item>
        /// <item><description><c>path</c> - The full hierarchy path of the new node</description></item>
        /// </list>
        /// </returns>
        [XRMcp]
        [McpName("create_scene_node")]
        [Description("Create a scene node in the active world/scene.")]
        public static Task<McpToolResponse> CreateSceneNodeAsync(
            McpToolContext context,
            [McpName("name"), Description("Name for the new scene node.")] string name,
            [McpName("parent_id"), Description("Optional parent scene node ID.")]
            string? parentId = null,
            [McpName("scene_name"), Description("Optional scene name; defaults to the first active scene.")]
            string? sceneName = null)
        {
            var world = context.WorldInstance;
            var scene = ResolveScene(world, sceneName);
            if (scene is null)
                return Task.FromResult(new McpToolResponse("No active scene found to create the node.", isError: true));

            SceneNode node;
            if (!string.IsNullOrWhiteSpace(parentId))
            {
                if (!TryGetNodeById(world, parentId!, out var parent, out var error) || parent is null)
                    return Task.FromResult(new McpToolResponse(error ?? "Parent node not found.", isError: true));

                node = parent!.NewChild(name);
            }
            else
            {
                node = new SceneNode(name);
                scene.RootNodes.Add(node);
                if (scene.IsVisible)
                    world.RootNodes.Add(node);
            }

            return Task.FromResult(new McpToolResponse($"Created scene node '{name}'.", new { id = node.ID, path = BuildNodePath(node) }));
        }

        /// <summary>
        /// Renames a scene node.
        /// </summary>
        [XRMcp]
        [McpName("rename_scene_node")]
        [Description("Rename a scene node by ID.")]
        public static Task<McpToolResponse> RenameSceneNodeAsync(
            McpToolContext context,
            [McpName("node_id"), Description("Scene node ID to rename.")] string nodeId,
            [McpName("name"), Description("New display name for the node.")] string name)
        {
            if (!TryGetNodeById(context.WorldInstance, nodeId, out var node, out var error) || node is null)
                return Task.FromResult(new McpToolResponse(error ?? "Scene node not found.", isError: true));

            if (string.IsNullOrWhiteSpace(name))
                name = SceneNode.DefaultName;

            node.Name = name;
            return Task.FromResult(new McpToolResponse($"Renamed '{nodeId}' to '{name}'."));
        }

        /// <summary>
        /// Duplicates a scene node, optionally including children.
        /// </summary>
        [XRMcp]
        [McpName("duplicate_scene_node")]
        [Description("Duplicate a scene node (optionally with children).")]
        public static Task<McpToolResponse> DuplicateSceneNodeAsync(
            McpToolContext context,
            [McpName("node_id"), Description("Scene node ID to duplicate.")] string nodeId,
            [McpName("new_name"), Description("Optional name for the duplicated node.")] string? newName = null,
            [McpName("parent_id"), Description("Optional parent ID for the duplicated node.")] string? parentId = null,
            [McpName("include_children"), Description("Whether to duplicate children.")] bool includeChildren = true,
            [McpName("preserve_world_transform"), Description("Preserve world transform when reparenting.")] bool preserveWorldTransform = false)
        {
            var world = context.WorldInstance;
            if (!TryGetNodeById(world, nodeId, out var sourceNode, out var error) || sourceNode is null)
                return Task.FromResult(new McpToolResponse(error ?? "Scene node not found.", isError: true));

            SceneNode clone = SceneNodePrefabUtility.CloneHierarchy(sourceNode);
            if (!includeChildren)
            {
                var children = GetChildren(clone).ToArray();
                foreach (var child in children)
                {
                    clone.RemoveChild(child);
                    child.Destroy();
                }
            }

            if (!string.IsNullOrWhiteSpace(newName))
                clone.Name = newName;

            SceneNode? newParent = null;
            if (!string.IsNullOrWhiteSpace(parentId))
            {
                if (!TryGetNodeById(world, parentId!, out newParent, out var parentError) || newParent is null)
                    return Task.FromResult(new McpToolResponse(parentError ?? "Parent node not found.", isError: true));

                clone.Transform.SetParent(newParent.Transform, preserveWorldTransform, EParentAssignmentMode.Immediate);
            }

            if (clone.Parent is null)
            {
                var targetScene = newParent is not null
                    ? FindSceneForNode(newParent, world)
                    : FindSceneForNode(sourceNode, world);

                if (targetScene is null)
                    targetScene = ResolveScene(world, null);

                if (targetScene is null)
                    return Task.FromResult(new McpToolResponse("No active scene found to place the duplicated node.", isError: true));

                targetScene.RootNodes.Add(clone);
                if (targetScene.IsVisible)
                    world.RootNodes.Add(clone);
            }

            return Task.FromResult(new McpToolResponse($"Duplicated scene node '{nodeId}'.", new { id = clone.ID, path = BuildNodePath(clone) }));
        }

        /// <summary>
        /// Reorders a scene node among its siblings.
        /// </summary>
        [XRMcp]
        [McpName("move_node_sibling")]
        [Description("Reorder a scene node among siblings.")]
        public static Task<McpToolResponse> MoveNodeSiblingAsync(
            McpToolContext context,
            [McpName("node_id"), Description("Scene node ID to move.")] string nodeId,
            [McpName("new_index"), Description("New sibling index (0-based).")]
            int? newIndex = null,
            [McpName("after_node_id"), Description("Place after this sibling node ID.")]
            string? afterNodeId = null)
        {
            var world = context.WorldInstance;
            if (!TryGetNodeById(world, nodeId, out var node, out var error) || node is null)
                return Task.FromResult(new McpToolResponse(error ?? "Scene node not found.", isError: true));

            if (!newIndex.HasValue && string.IsNullOrWhiteSpace(afterNodeId))
                return Task.FromResult(new McpToolResponse("Provide new_index or after_node_id.", isError: true));

            if (node.Parent is SceneNode parent)
            {
                var siblings = parent.Transform.Children;
                int currentIndex = siblings.IndexOf(node.Transform);
                if (currentIndex < 0)
                    return Task.FromResult(new McpToolResponse("Node is not a child of its parent transform.", isError: true));

                int targetIndex;
                if (!string.IsNullOrWhiteSpace(afterNodeId))
                {
                    if (!TryGetNodeById(world, afterNodeId!, out var afterNode, out var afterError) || afterNode is null)
                        return Task.FromResult(new McpToolResponse(afterError ?? "After-node not found.", isError: true));

                    if (!ReferenceEquals(afterNode.Parent, parent))
                        return Task.FromResult(new McpToolResponse("After-node must share the same parent.", isError: true));

                    targetIndex = siblings.IndexOf(afterNode.Transform) + 1;
                }
                else
                {
                    targetIndex = Math.Clamp(newIndex!.Value, 0, siblings.Count - 1);
                }

                parent.RemoveChild(node);
                if (targetIndex > siblings.Count)
                    targetIndex = siblings.Count;
                parent.InsertChild(node, targetIndex);

                return Task.FromResult(new McpToolResponse($"Moved '{nodeId}' to sibling index {targetIndex}."));
            }

            var scene = FindSceneForNode(node, world);
            if (scene is null)
                return Task.FromResult(new McpToolResponse("Unable to resolve the owning scene for the root node.", isError: true));

            int rootIndex = scene.RootNodes.IndexOf(node);
            if (rootIndex < 0)
                return Task.FromResult(new McpToolResponse("Node is not registered as a root in its scene.", isError: true));

            int targetRootIndex;
            if (!string.IsNullOrWhiteSpace(afterNodeId))
            {
                if (!TryGetNodeById(world, afterNodeId!, out var afterRoot, out var afterError) || afterRoot is null)
                    return Task.FromResult(new McpToolResponse(afterError ?? "After-node not found.", isError: true));

                if (afterRoot.Parent is not null || !scene.RootNodes.Contains(afterRoot))
                    return Task.FromResult(new McpToolResponse("After-node must be a root node in the same scene.", isError: true));

                targetRootIndex = scene.RootNodes.IndexOf(afterRoot) + 1;
            }
            else
            {
                targetRootIndex = Math.Clamp(newIndex!.Value, 0, scene.RootNodes.Count - 1);
            }

            scene.RootNodes.Remove(node);
            if (targetRootIndex > scene.RootNodes.Count)
                targetRootIndex = scene.RootNodes.Count;
            scene.RootNodes.Insert(targetRootIndex, node);

            if (scene.IsVisible)
            {
                var worldRoots = world.RootNodes;
                foreach (var root in scene.RootNodes)
                    worldRoots.Remove(root);

                foreach (var root in scene.RootNodes)
                    worldRoots.Add(root);
            }

            return Task.FromResult(new McpToolResponse($"Moved root '{nodeId}' to index {targetRootIndex}.", new { index = targetRootIndex }));
        }

        /// <summary>
        /// Sets whether a scene node (and optionally its children) is active in the hierarchy.
        /// </summary>
        [XRMcp]
        [McpName("set_node_active_recursive")]
        [Description("Set active state on a node and its children.")]
        public static Task<McpToolResponse> SetNodeActiveRecursiveAsync(
            McpToolContext context,
            [McpName("node_id"), Description("Scene node ID to update.")] string nodeId,
            [McpName("is_active"), Description("Whether the node is active.")] bool isActive)
        {
            if (!TryGetNodeById(context.WorldInstance, nodeId, out var node, out var error) || node is null)
                return Task.FromResult(new McpToolResponse(error ?? "Scene node not found.", isError: true));

            foreach (var entry in EnumerateHierarchy(node))
                entry.IsActiveSelf = isActive;

            return Task.FromResult(new McpToolResponse($"Set active={isActive} for '{nodeId}' and its children."));
        }

        /// <summary>
        /// Finds scene nodes by name in the active world.
        /// </summary>
        [XRMcp]
        [McpName("find_nodes_by_name")]
        [Description("Find scene nodes by name (exact or contains).")]
        public static Task<McpToolResponse> FindNodesByNameAsync(
            McpToolContext context,
            [McpName("name"), Description("Name to search for.")] string name,
            [McpName("scene_name"), Description("Optional scene name or ID.")] string? sceneName = null,
            [McpName("match_mode"), Description("Match mode: exact or contains.")] string matchMode = "contains")
        {
            if (string.IsNullOrWhiteSpace(name))
                return Task.FromResult(new McpToolResponse("Name must be provided.", isError: true));

            var world = context.WorldInstance;
            var scenes = new List<XRScene>();

            if (!string.IsNullOrWhiteSpace(sceneName))
            {
                if (!TryResolveScene(world, sceneName, out var scene, out var error) || scene is null)
                    return Task.FromResult(new McpToolResponse(error ?? "Scene not found.", isError: true));
                scenes.Add(scene);
            }
            else if (world.TargetWorld is not null)
            {
                scenes.AddRange(world.TargetWorld.Scenes);
            }

            bool exact = string.Equals(matchMode, "exact", StringComparison.OrdinalIgnoreCase);
            var results = new List<object>();

            foreach (var scene in scenes)
            {
                foreach (var root in scene.RootNodes)
                {
                    if (root is null) continue;
                    foreach (var node in EnumerateHierarchy(root))
                    {
                        string nodeName = node.Name ?? SceneNode.DefaultName;
                        bool match = exact
                            ? string.Equals(nodeName, name, StringComparison.OrdinalIgnoreCase)
                            : nodeName.Contains(name, StringComparison.OrdinalIgnoreCase);

                        if (!match)
                            continue;

                        results.Add(new
                        {
                            id = node.ID,
                            name = nodeName,
                            path = BuildNodePath(node)
                        });
                    }
                }
            }

            return Task.FromResult(new McpToolResponse($"Found {results.Count} nodes matching '{name}'.", new { nodes = results }));
        }

        /// <summary>
        /// Finds scene nodes by component type name.
        /// </summary>
        [XRMcp]
        [McpName("find_nodes_by_type")]
        [Description("Find scene nodes that have a component type.")]
        public static Task<McpToolResponse> FindNodesByTypeAsync(
            McpToolContext context,
            [McpName("component_type"), Description("Component type name to search for.")] string componentType,
            [McpName("scene_name"), Description("Optional scene name or ID.")] string? sceneName = null)
        {
            if (string.IsNullOrWhiteSpace(componentType))
                return Task.FromResult(new McpToolResponse("Component type must be provided.", isError: true));

            var world = context.WorldInstance;
            var scenes = new List<XRScene>();

            if (!string.IsNullOrWhiteSpace(sceneName))
            {
                if (!TryResolveScene(world, sceneName, out var scene, out var error) || scene is null)
                    return Task.FromResult(new McpToolResponse(error ?? "Scene not found.", isError: true));
                scenes.Add(scene);
            }
            else if (world.TargetWorld is not null)
            {
                scenes.AddRange(world.TargetWorld.Scenes);
            }

            bool resolved = McpToolRegistry.TryResolveComponentType(componentType, out var type);
            var results = new List<object>();

            foreach (var scene in scenes)
            {
                foreach (var root in scene.RootNodes)
                {
                    if (root is null) continue;
                    foreach (var node in EnumerateHierarchy(root))
                    {
                        bool match = resolved
                            ? node.Components.Any(comp => type!.IsInstanceOfType(comp))
                            : node.Components.Any(comp => string.Equals(comp.GetType().Name, componentType, StringComparison.OrdinalIgnoreCase));

                        if (!match)
                            continue;

                        results.Add(new
                        {
                            id = node.ID,
                            name = node.Name ?? SceneNode.DefaultName,
                            path = BuildNodePath(node)
                        });
                    }
                }
            }

            return Task.FromResult(new McpToolResponse($"Found {results.Count} nodes with component '{componentType}'.", new { nodes = results }));
        }

        /// <summary>
        /// Selects one or more scene nodes in the editor.
        /// </summary>
        [XRMcp]
        [McpName("select_node")]
        [Description("Select one or more scene nodes in the editor.")]
        public static Task<McpToolResponse> SelectNodeAsync(
            McpToolContext context,
            [McpName("node_id"), Description("Scene node ID to select.")] string? nodeId = null,
            [McpName("node_ids"), Description("Optional list of scene node IDs to select.")] string[]? nodeIds = null,
            [McpName("append"), Description("Append to existing selection.")] bool append = false)
        {
            var world = context.WorldInstance;
            var targets = new List<SceneNode>();

            if (!string.IsNullOrWhiteSpace(nodeId))
            {
                if (!TryGetNodeById(world, nodeId, out var node, out var error) || node is null)
                    return Task.FromResult(new McpToolResponse(error ?? "Scene node not found.", isError: true));
                targets.Add(node);
            }

            if (nodeIds is not null)
            {
                foreach (var id in nodeIds)
                {
                    if (!TryGetNodeById(world, id, out var node, out var error) || node is null)
                        return Task.FromResult(new McpToolResponse(error ?? $"Scene node '{id}' not found.", isError: true));
                    targets.Add(node);
                }
            }

            if (targets.Count == 0)
            {
                Selection.Clear();
                return Task.FromResult(new McpToolResponse("Cleared selection."));
            }

            if (append && Selection.SceneNodes.Length > 0)
                Selection.SceneNodes = [.. Selection.SceneNodes, .. targets.Distinct()];
            else
                Selection.SceneNodes = targets.Distinct().ToArray();

            return Task.FromResult(new McpToolResponse($"Selected {Selection.SceneNodes.Length} node(s)."));
        }

        /// <summary>
        /// Focuses the editor camera on a scene node.
        /// </summary>
        [XRMcp]
        [McpName("focus_node_in_view")]
        [Description("Focus the editor camera on a scene node.")]
        public static Task<McpToolResponse> FocusNodeInViewAsync(
            McpToolContext context,
            [McpName("node_id"), Description("Scene node ID to focus.")] string nodeId,
            [McpName("duration"), Description("Optional focus duration in seconds.")] float durationSeconds = 0.35f)
        {
            if (!TryGetNodeById(context.WorldInstance, nodeId, out var node, out var error) || node is null)
                return Task.FromResult(new McpToolResponse(error ?? "Scene node not found.", isError: true));

            var player = Engine.State.MainPlayer ?? Engine.State.GetOrCreateLocalPlayer(ELocalPlayerIndex.One);
            if (player?.ControlledPawn is not EditorFlyingCameraPawnComponent pawn)
                return Task.FromResult(new McpToolResponse("No editor camera pawn available to focus.", isError: true));

            pawn.FocusOnNode(node, durationSeconds);
            return Task.FromResult(new McpToolResponse($"Focused camera on '{nodeId}'."));
        }

        /// <summary>
        /// Sets a scene node's layer index.
        /// </summary>
        [XRMcp]
        [McpName("set_layer")]
        [Description("Set the layer for a scene node.")]
        public static Task<McpToolResponse> SetLayerAsync(
            McpToolContext context,
            [McpName("node_id"), Description("Scene node ID to update.")] string nodeId,
            [McpName("layer_index"), Description("Layer index (0-31).")]
            int? layerIndex = null,
            [McpName("layer_name"), Description("Optional layer name (Dynamic, Static, Gizmos).")]
            string? layerName = null)
        {
            if (!TryGetNodeById(context.WorldInstance, nodeId, out var node, out var error) || node is null)
                return Task.FromResult(new McpToolResponse(error ?? "Scene node not found.", isError: true));

            int layer;
            if (!string.IsNullOrWhiteSpace(layerName))
            {
                var entry = DefaultLayers.All.FirstOrDefault(kvp => string.Equals(kvp.Value, layerName, StringComparison.OrdinalIgnoreCase));
                if (entry.Value is null)
                    return Task.FromResult(new McpToolResponse($"Unknown layer '{layerName}'.", isError: true));
                layer = entry.Key;
            }
            else if (layerIndex.HasValue)
            {
                layer = layerIndex.Value;
            }
            else
            {
                return Task.FromResult(new McpToolResponse("Provide layer_index or layer_name.", isError: true));
            }

            node.Layer = layer;
            return Task.FromResult(new McpToolResponse($"Set layer for '{nodeId}' to {node.Layer}.", new { layer = node.Layer }));
        }

        /// <summary>
        /// Lists known layers and those in use by the active world.
        /// </summary>
        [XRMcp]
        [McpName("list_layers")]
        [Description("List known layers and layers used in the active world.")]
        public static Task<McpToolResponse> ListLayersAsync(
            McpToolContext context)
        {
            var world = context.WorldInstance;
            var used = new HashSet<int>();

            if (world.TargetWorld is not null)
            {
                foreach (var scene in world.TargetWorld.Scenes)
                {
                    foreach (var root in scene.RootNodes)
                    {                        if (root is null) continue;                        foreach (var node in EnumerateHierarchy(root))
                            used.Add(node.Layer);
                    }
                }
            }

            var layers = used.Select(index => new
            {
                index,
                name = DefaultLayers.All.TryGetValue(index, out var name) ? name : null,
                isDefault = DefaultLayers.All.ContainsKey(index)
            }).OrderBy(x => x.index).ToArray();

            return Task.FromResult(new McpToolResponse("Listed layers.", new { layers }));
        }

        /// <summary>
        /// Assigns or removes a tag on a scene node.
        /// </summary>
        [XRMcp]
        [McpName("set_tag")]
        [Description("Assign or remove a tag on a scene node.")]
        public static Task<McpToolResponse> SetTagAsync(
            McpToolContext context,
            [McpName("node_id"), Description("Scene node ID to update.")] string nodeId,
            [McpName("tag"), Description("Tag value to assign.")] string tag,
            [McpName("add"), Description("True to add; false to remove.")] bool add = true)
        {
            if (!TryGetNodeById(context.WorldInstance, nodeId, out var node, out var error) || node is null)
                return Task.FromResult(new McpToolResponse(error ?? "Scene node not found.", isError: true));

            if (string.IsNullOrWhiteSpace(tag))
                return Task.FromResult(new McpToolResponse("Tag must be provided.", isError: true));

            var tags = GetOrCreateTags(node);
            if (add)
                tags.Add(tag);
            else
                tags.Remove(tag);

            return Task.FromResult(new McpToolResponse($"{(add ? "Added" : "Removed")} tag '{tag}' on '{nodeId}'.", new { tags = tags.ToArray() }));
        }

        /// <summary>
        /// Lists tags on a node or across the active world.
        /// </summary>
        [XRMcp]
        [McpName("list_tags")]
        [Description("List tags on a node or across the active world.")]
        public static Task<McpToolResponse> ListTagsAsync(
            McpToolContext context,
            [McpName("node_id"), Description("Optional scene node ID to list tags for.")] string? nodeId = null)
        {
            var world = context.WorldInstance;

            if (!string.IsNullOrWhiteSpace(nodeId))
            {
                if (!TryGetNodeById(world, nodeId, out var node, out var error) || node is null)
                    return Task.FromResult(new McpToolResponse(error ?? "Scene node not found.", isError: true));

                var tags = GetTags(node).ToArray();
                return Task.FromResult(new McpToolResponse($"Listed tags for '{nodeId}'.", new { tags }));
            }

            var allTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (world.TargetWorld is not null)
            {
                foreach (var scene in world.TargetWorld.Scenes)
                {
                    foreach (var root in scene.RootNodes)
                    {
                        if (root is null) continue;
                        foreach (var node in EnumerateHierarchy(root))
                        {
                            foreach (var tag in GetTags(node))
                                allTags.Add(tag);
                        }
                    }
                }
            }

            return Task.FromResult(new McpToolResponse("Listed tags.", new { tags = allTags.OrderBy(x => x).ToArray() }));
        }

        /// <summary>
        /// Sets a scene node's transform properties (translation, rotation, and/or scale).
        /// </summary>
        [XRMcp]
        [McpName("set_node_transform")]
        [Description("Set a scene node transform (translation, rotation, scale).")]
        public static Task<McpToolResponse> SetNodeTransformAsync(
            McpToolContext context,
            [McpName("node_id"), Description("Scene node ID to update.")] string nodeId,
            [McpName("translation_x"), Description("Local/world translation X.")] float? translationX = null,
            [McpName("translation_y"), Description("Local/world translation Y.")] float? translationY = null,
            [McpName("translation_z"), Description("Local/world translation Z.")] float? translationZ = null,
            [McpName("pitch"), Description("Rotation pitch in degrees.")] float? pitch = null,
            [McpName("yaw"), Description("Rotation yaw in degrees.")] float? yaw = null,
            [McpName("roll"), Description("Rotation roll in degrees.")] float? roll = null,
            [McpName("scale_x"), Description("Local scale X.")] float? scaleX = null,
            [McpName("scale_y"), Description("Local scale Y.")] float? scaleY = null,
            [McpName("scale_z"), Description("Local scale Z.")] float? scaleZ = null,
            [McpName("space"), Description("Transform space: local or world.")] string space = "local")
            => SetTransformAsync(context, nodeId, translationX, translationY, translationZ, pitch, yaw, roll, scaleX, scaleY, scaleZ, space);

        /// <summary>
        /// Sets a scene node's world transform properties.
        /// </summary>
        [XRMcp]
        [McpName("set_node_world_transform")]
        [Description("Set a scene node world transform (translation, rotation, scale).")]
        public static Task<McpToolResponse> SetNodeWorldTransformAsync(
            McpToolContext context,
            [McpName("node_id"), Description("Scene node ID to update.")] string nodeId,
            [McpName("translation_x"), Description("World translation X.")] float? translationX = null,
            [McpName("translation_y"), Description("World translation Y.")] float? translationY = null,
            [McpName("translation_z"), Description("World translation Z.")] float? translationZ = null,
            [McpName("pitch"), Description("World rotation pitch in degrees.")] float? pitch = null,
            [McpName("yaw"), Description("World rotation yaw in degrees.")] float? yaw = null,
            [McpName("roll"), Description("World rotation roll in degrees.")] float? roll = null,
            [McpName("scale_x"), Description("World scale X.")] float? scaleX = null,
            [McpName("scale_y"), Description("World scale Y.")] float? scaleY = null,
            [McpName("scale_z"), Description("World scale Z.")] float? scaleZ = null)
        {
            if (!TryGetNodeById(context.WorldInstance, nodeId, out var node, out var error) || node is null)
                return Task.FromResult(new McpToolResponse(error ?? "Scene node not found.", isError: true));

            if (node.Transform is not Transform transform)
                return Task.FromResult(new McpToolResponse($"Scene node '{nodeId}' does not use a standard Transform.", isError: true));

            var worldTranslation = transform.WorldTranslation;
            if (translationX.HasValue) worldTranslation.X = translationX.Value;
            if (translationY.HasValue) worldTranslation.Y = translationY.Value;
            if (translationZ.HasValue) worldTranslation.Z = translationZ.Value;

            var worldRotator = Rotator.FromQuaternion(transform.WorldRotation);
            if (pitch.HasValue) worldRotator.Pitch = pitch.Value;
            if (yaw.HasValue) worldRotator.Yaw = yaw.Value;
            if (roll.HasValue) worldRotator.Roll = roll.Value;

            if (translationX.HasValue || translationY.HasValue || translationZ.HasValue || pitch.HasValue || yaw.HasValue || roll.HasValue)
                transform.SetWorldTranslationRotation(worldTranslation, worldRotator.ToQuaternion());

            if (scaleX.HasValue || scaleY.HasValue || scaleZ.HasValue)
            {
                var worldScale = transform.LossyWorldScale;
                if (scaleX.HasValue) worldScale.X = scaleX.Value;
                if (scaleY.HasValue) worldScale.Y = scaleY.Value;
                if (scaleZ.HasValue) worldScale.Z = scaleZ.Value;

                var parentScale = transform.Parent?.LossyWorldScale ?? Vector3.One;
                var localScale = new Vector3(
                    parentScale.X == 0 ? worldScale.X : worldScale.X / parentScale.X,
                    parentScale.Y == 0 ? worldScale.Y : worldScale.Y / parentScale.Y,
                    parentScale.Z == 0 ? worldScale.Z : worldScale.Z / parentScale.Z);

                transform.Scale = localScale;
            }

            return Task.FromResult(new McpToolResponse($"Updated world transform for '{nodeId}'."));
        }

        /// <summary>
        /// Retrieves a scene node's world transform.
        /// </summary>
        [XRMcp]
        [McpName("get_node_world_transform")]
        [Description("Get a scene node's world transform (translation, rotation, scale).")]
        public static Task<McpToolResponse> GetNodeWorldTransformAsync(
            McpToolContext context,
            [McpName("node_id"), Description("Scene node ID to inspect.")] string nodeId)
        {
            if (!TryGetNodeById(context.WorldInstance, nodeId, out var node, out var error) || node is null)
                return Task.FromResult(new McpToolResponse(error ?? "Scene node not found.", isError: true));

            var transform = node.Transform;
            var rotator = Rotator.FromQuaternion(transform.WorldRotation);
            var info = new
            {
                translation = transform.WorldTranslation,
                rotation = new { pitch = rotator.Pitch, yaw = rotator.Yaw, roll = rotator.Roll },
                scale = transform.LossyWorldScale
            };

            return Task.FromResult(new McpToolResponse($"Retrieved world transform for '{nodeId}'.", info));
        }

        /// <summary>
        /// Creates a prefab asset from a scene node hierarchy.
        /// </summary>
        [XRMcp]
        [McpName("create_prefab_from_node")]
        [Description("Create a prefab asset from a scene node hierarchy.")]
        public static Task<McpToolResponse> CreatePrefabFromNodeAsync(
            McpToolContext context,
            [McpName("node_id"), Description("Scene node ID to use as prefab root.")] string nodeId,
            [McpName("asset_name"), Description("Prefab asset name.")] string assetName,
            [McpName("target_directory"), Description("Target directory for the prefab asset.")] string targetDirectory)
        {
            if (!TryGetNodeById(context.WorldInstance, nodeId, out var node, out var error) || node is null)
                return Task.FromResult(new McpToolResponse(error ?? "Scene node not found.", isError: true));

            var prefab = SceneNodePrefabService.CreatePrefabAsset(node, assetName, targetDirectory);
            return Task.FromResult(new McpToolResponse($"Created prefab '{assetName}'.", new { id = prefab.ID, path = prefab.FilePath }));
        }

        /// <summary>
        /// Instantiates a prefab into the active scene.
        /// </summary>
        [XRMcp]
        [McpName("instantiate_prefab")]
        [Description("Instantiate a prefab into the active scene.")]
        public static Task<McpToolResponse> InstantiatePrefabAsync(
            McpToolContext context,
            [McpName("asset_path"), Description("Prefab asset path to load.")] string? assetPath = null,
            [McpName("asset_id"), Description("Prefab asset ID to load.")] string? assetId = null,
            [McpName("parent_id"), Description("Optional parent node ID.")] string? parentId = null,
            [McpName("scene_name"), Description("Optional scene name or ID.")] string? sceneName = null,
            [McpName("preserve_world_transform"), Description("Preserve world transform when parenting.")] bool preserveWorldTransform = false)
        {
            var world = context.WorldInstance;
            var assets = Engine.Assets;
            if (assets is null)
                return Task.FromResult(new McpToolResponse("Asset manager is unavailable.", isError: true));

            SceneNode? parent = null;
            if (!string.IsNullOrWhiteSpace(parentId))
            {
                if (!TryGetNodeById(world, parentId!, out parent, out var parentError) || parent is null)
                    return Task.FromResult(new McpToolResponse(parentError ?? "Parent node not found.", isError: true));
            }

            SceneNode? instance = null;
            if (!string.IsNullOrWhiteSpace(assetPath))
            {
                instance = assets.InstantiatePrefab(assetPath!, world, parent, preserveWorldTransform);
            }
            else if (!string.IsNullOrWhiteSpace(assetId) && Guid.TryParse(assetId, out var prefabGuid))
            {
                instance = assets.InstantiatePrefab(prefabGuid, world, parent, preserveWorldTransform);
            }
            else
            {
                return Task.FromResult(new McpToolResponse("Provide asset_path or asset_id.", isError: true));
            }

            if (instance is null)
                return Task.FromResult(new McpToolResponse("Failed to instantiate prefab.", isError: true));

            if (instance.Parent is null)
            {
                var scene = ResolveScene(world, sceneName);
                if (scene is null)
                    return Task.FromResult(new McpToolResponse("No active scene found to place the prefab.", isError: true));

                scene.RootNodes.Add(instance);
                if (scene.IsVisible)
                    world.RootNodes.Add(instance);
            }

            return Task.FromResult(new McpToolResponse("Instantiated prefab.", new { id = instance.ID, path = BuildNodePath(instance) }));
        }
    }
}
