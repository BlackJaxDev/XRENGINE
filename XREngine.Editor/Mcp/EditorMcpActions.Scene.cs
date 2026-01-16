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
        [XRMCP]
        [DisplayName("list_scene_nodes")]
        [Description("List scene nodes in the active world/scene.")]
        public static Task<McpToolResponse> ListSceneNodesAsync(
            McpToolContext context,
            [DisplayName("scene_name"), Description("Optional scene name; defaults to the first active scene.")]
            string? sceneName = null,
            [DisplayName("max_depth"), Description("Optional max depth to traverse.")] int? maxDepth = null)
        {
            var scene = ResolveScene(context.WorldInstance, sceneName);
            if (scene is null)
                return Task.FromResult(new McpToolResponse("No active scene found.", isError: true));

            var nodes = new List<object>();
            foreach (var root in scene.RootNodes)
                CollectNodes(root, nodes, 0, maxDepth);

            return Task.FromResult(new McpToolResponse("Listed scene nodes.", new { nodes }));
        }

        [XRMCP]
        [DisplayName("get_scene_node_info")]
        [Description("Get detailed info about a scene node, including transform and components.")]
        public static Task<McpToolResponse> GetSceneNodeInfoAsync(
            McpToolContext context,
            [DisplayName("node_id"), Description("Scene node ID to inspect.")] string nodeId)
        {
            if (!TryGetNodeById(context.WorldInstance, nodeId, out var node, out var error))
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

        [XRMCP]
        [DisplayName("set_node_active")]
        [Description("Set whether a scene node is active in the hierarchy.")]
        public static Task<McpToolResponse> SetNodeActiveAsync(
            McpToolContext context,
            [DisplayName("node_id"), Description("Scene node ID to update.")] string nodeId,
            [DisplayName("is_active"), Description("Whether the node is active.")] bool isActive)
        {
            if (!TryGetNodeById(context.WorldInstance, nodeId, out var node, out var error))
                return Task.FromResult(new McpToolResponse(error ?? "Scene node not found.", isError: true));

            node.IsActiveSelf = isActive;
            return Task.FromResult(new McpToolResponse($"Set '{nodeId}' active={isActive}."));
        }

        [XRMCP]
        [DisplayName("reparent_node")]
        [Description("Reparent a scene node to a new parent.")]
        public static Task<McpToolResponse> ReparentNodeAsync(
            McpToolContext context,
            [DisplayName("node_id"), Description("Scene node ID to reparent.")] string nodeId,
            [DisplayName("new_parent_id"), Description("New parent node ID (omit to unparent).")]
            string? newParentId = null,
            [DisplayName("preserve_world_transform"), Description("Preserve world transform when reparenting.")] bool preserveWorldTransform = false)
        {
            if (!TryGetNodeById(context.WorldInstance, nodeId, out var node, out var error))
                return Task.FromResult(new McpToolResponse(error ?? "Scene node not found.", isError: true));

            SceneNode? newParent = null;
            if (!string.IsNullOrWhiteSpace(newParentId))
            {
                if (!TryGetNodeById(context.WorldInstance, newParentId!, out newParent, out var parentError))
                    return Task.FromResult(new McpToolResponse(parentError ?? "Parent node not found.", isError: true));
            }

            node.Transform.SetParent(newParent?.Transform, preserveWorldTransform, EParentAssignmentMode.Immediate);
            return Task.FromResult(new McpToolResponse($"Reparented '{nodeId}' to '{newParentId ?? "<root>"}'."));
        }

        [XRMCP]
        [DisplayName("delete_scene_node")]
        [Description("Delete a scene node and its hierarchy.")]
        public static Task<McpToolResponse> DeleteSceneNodeAsync(
            McpToolContext context,
            [DisplayName("node_id"), Description("Scene node ID to delete.")] string nodeId)
        {
            if (!TryGetNodeById(context.WorldInstance, nodeId, out var node, out var error))
                return Task.FromResult(new McpToolResponse(error ?? "Scene node not found.", isError: true));

            node.Destroy();
            return Task.FromResult(new McpToolResponse($"Deleted scene node '{nodeId}'."));
        }

        [XRMCP]
        [DisplayName("create_scene_node")]
        [Description("Create a scene node in the active world/scene.")]
        public static Task<McpToolResponse> CreateSceneNodeAsync(
            McpToolContext context,
            [DisplayName("name"), Description("Name for the new scene node.")] string name,
            [DisplayName("parent_id"), Description("Optional parent scene node ID.")]
            string? parentId = null,
            [DisplayName("scene_name"), Description("Optional scene name; defaults to the first active scene.")]
            string? sceneName = null)
        {
            var world = context.WorldInstance;
            var scene = ResolveScene(world, sceneName);
            if (scene is null)
                return Task.FromResult(new McpToolResponse("No active scene found to create the node.", isError: true));

            SceneNode node;
            if (!string.IsNullOrWhiteSpace(parentId))
            {
                if (!TryGetNodeById(world, parentId!, out var parent, out var error))
                    return Task.FromResult(new McpToolResponse(error ?? "Parent node not found.", isError: true));

                node = parent!.NewChild(name);
                node.World = parent.World;
            }
            else
            {
                node = new SceneNode(name);
                scene.RootNodes.Add(node);
                if (scene.IsVisible)
                {
                    node.World = world;
                    world.RootNodes.Add(node);
                }
            }

            return Task.FromResult(new McpToolResponse($"Created scene node '{name}'.", new { id = node.ID, path = BuildNodePath(node) }));
        }
    }
}
