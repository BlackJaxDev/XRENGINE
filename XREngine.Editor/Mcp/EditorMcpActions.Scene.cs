using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using XREngine.Data.Core;
using XREngine.Data.Transforms.Rotations;
using XREngine.Scene;
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
                CollectNodes(root, nodes, 0, maxDepth);

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
    }
}
