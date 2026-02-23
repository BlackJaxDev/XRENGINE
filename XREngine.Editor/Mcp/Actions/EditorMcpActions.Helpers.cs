using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor.Mcp
{
    public sealed partial class EditorMcpActions
    {
        private static readonly ConditionalWeakTable<SceneNode, HashSet<string>> NodeTags = [];

        private static bool TryGetNodeById(XRWorldInstance world, string nodeId, out SceneNode? node, out string? error)
        {
            node = null;
            error = null;

            if (!Guid.TryParse(nodeId, out var guid))
            {
                error = $"Invalid node_id '{nodeId}'.";
                return false;
            }

            if (!XRObjectBase.ObjectsCache.TryGetValue(guid, out var obj) || obj is not SceneNode sceneNode)
            {
                error = $"Scene node '{nodeId}' not found.";
                return false;
            }

            if (sceneNode.World != world)
            {
                error = $"Scene node '{nodeId}' is not part of the active world instance.";
                return false;
            }

            node = sceneNode;
            return true;
        }

        private static XRComponent? FindComponent(SceneNode node, string? componentId, string? componentName, string? componentTypeName, out string? error)
        {
            error = null;

            if (!string.IsNullOrWhiteSpace(componentId))
            {
                if (!Guid.TryParse(componentId, out var guid))
                {
                    error = $"Invalid component_id '{componentId}'.";
                    return null;
                }

                if (!XRObjectBase.ObjectsCache.TryGetValue(guid, out var obj) || obj is not XRComponent component)
                {
                    error = $"Component '{componentId}' not found.";
                    return null;
                }

                if (!ReferenceEquals(component.SceneNode, node))
                {
                    error = $"Component '{componentId}' is not on the specified node.";
                    return null;
                }

                return component;
            }

            var components = node.Components;
            if (!string.IsNullOrWhiteSpace(componentName))
                return components.FirstOrDefault(comp => string.Equals(comp.Name, componentName, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(componentTypeName))
            {
                if (McpToolRegistry.TryResolveComponentType(componentTypeName, out var type))
                    return components.FirstOrDefault(comp => type.IsInstanceOfType(comp));

                return components.FirstOrDefault(comp => string.Equals(comp.GetType().Name, componentTypeName, StringComparison.OrdinalIgnoreCase));
            }

            return null;
        }

        private static XRScene? ResolveScene(XRWorldInstance world, string? sceneName)
        {
            var targetWorld = world.TargetWorld;
            if (targetWorld is null)
                return null;

            if (!string.IsNullOrWhiteSpace(sceneName))
                return targetWorld.Scenes.FirstOrDefault(scene => string.Equals(scene.Name, sceneName, StringComparison.OrdinalIgnoreCase));

            return targetWorld.Scenes.FirstOrDefault();
        }

        private static bool TryResolveScene(XRWorldInstance world, string? sceneIdOrName, out XRScene? scene, out string? error)
        {
            scene = null;
            error = null;

            var targetWorld = world.TargetWorld;
            if (targetWorld is null)
            {
                error = "No active world found.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(sceneIdOrName))
            {
                scene = targetWorld.Scenes.FirstOrDefault();
                if (scene is null)
                {
                    error = "No scenes are available in the active world.";
                    return false;
                }
                return true;
            }

            if (Guid.TryParse(sceneIdOrName, out var guid))
            {
                scene = targetWorld.Scenes.FirstOrDefault(x => x.ID == guid);
                if (scene is null)
                {
                    error = $"Scene '{sceneIdOrName}' not found.";
                    return false;
                }
                return true;
            }

            scene = targetWorld.Scenes.FirstOrDefault(x => string.Equals(x.Name, sceneIdOrName, StringComparison.OrdinalIgnoreCase));
            if (scene is null)
            {
                error = $"Scene '{sceneIdOrName}' not found.";
                return false;
            }

            return true;
        }

        private static IEnumerable<SceneNode> GetChildren(SceneNode node)
        {
            foreach (var childTransform in node.Transform.Children)
            {
                var childNode = childTransform.SceneNode;
                if (childNode is not null)
                    yield return childNode;
            }
        }

        private static IEnumerable<SceneNode> EnumerateHierarchy(SceneNode root)
        {
            yield return root;

            foreach (var child in GetChildren(root))
            {
                foreach (var descendant in EnumerateHierarchy(child))
                    yield return descendant;
            }
        }

        private static SceneNode? GetHierarchyRoot(SceneNode node)
        {
            TransformBase? transform = node.Transform;
            while (transform?.Parent is TransformBase parent)
                transform = parent;
            return transform?.SceneNode;
        }

        private static XRScene? FindSceneForNode(SceneNode node, XRWorldInstance world)
        {
            var targetWorld = world.TargetWorld;
            if (targetWorld is null)
                return null;

            SceneNode? root = GetHierarchyRoot(node);
            if (root is null)
                return null;

            foreach (var scene in targetWorld.Scenes)
            {
                if (scene.RootNodes.Contains(root))
                    return scene;
            }

            return null;
        }

        private static HashSet<string> GetOrCreateTags(SceneNode node)
        {
            if (!NodeTags.TryGetValue(node, out var tags))
            {
                tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                NodeTags.Add(node, tags);
            }

            return tags;
        }

        private static IReadOnlyCollection<string> GetTags(SceneNode node)
            => NodeTags.TryGetValue(node, out var tags) ? tags : Array.Empty<string>();

        private static string BuildNodePath(SceneNode node)
        {
            var parts = new List<string>();
            SceneNode? current = node;
            while (current is not null)
            {
                parts.Add(current.Name ?? SceneNode.DefaultName);
                current = current.Parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static void CollectNodes(SceneNode node, ICollection<object> list, int depth, int? maxDepth)
        {
            list.Add(new
            {
                id = node.ID,
                name = node.Name,
                path = BuildNodePath(node),
                depth,
                childCount = node.Transform.Children.Count,
                isActive = node.IsActiveSelf
            });

            if (maxDepth.HasValue && depth >= maxDepth.Value)
                return;

            foreach (var child in GetChildren(node))
                CollectNodes(child, list, depth + 1, maxDepth);
        }
    }
}
