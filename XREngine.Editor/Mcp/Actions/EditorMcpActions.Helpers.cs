using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Vectors;
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

            SceneNode? sceneNode = FindNodeInWorld(world, guid);
            if (sceneNode is null
                && XRObjectBase.ObjectsCache.TryGetValue(guid, out var obj)
                && obj is SceneNode cachedNode)
            {
                sceneNode = cachedNode;
            }

            if (sceneNode is null)
            {
                error = $"Scene node '{nodeId}' not found.";
                return false;
            }

            // Inactive nodes are deliberately detached from the runtime world context, but
            // remain structurally owned by a scene in the active target world. MCP scene
            // operations must still be able to inspect and reactivate those nodes.
            if (!ReferenceEquals(sceneNode.World, world) && FindSceneForNode(sceneNode, world) is null)
            {
                error = $"Scene node '{nodeId}' is not part of the active world instance.";
                return false;
            }

            node = sceneNode;
            return true;
        }

        private static SceneNode? FindNodeInWorld(XRWorldInstance world, Guid nodeId)
        {
            if (world.TargetWorld is not null)
            {
                foreach (var scene in world.TargetWorld.Scenes)
                {
                    foreach (var root in scene.RootNodes)
                    {
                        if (root is null)
                            continue;

                        foreach (var node in EnumerateHierarchy(root))
                        {
                            if (node.ID == nodeId)
                                return node;
                        }
                    }
                }
            }

            // Runtime/editor-only roots are not necessarily part of TargetWorld.Scenes.
            foreach (var root in world.RootNodes)
            {
                if (root is null)
                    continue;

                foreach (var node in EnumerateHierarchy(root))
                {
                    if (node.ID == nodeId)
                        return node;
                }
            }

            return null;
        }

        internal static XRComponent? FindComponent(SceneNode node, string? componentId, string? componentName, string? componentTypeName, out string? error)
        {
            error = null;
            var components = node.Components;

            if (!string.IsNullOrWhiteSpace(componentId))
            {
                if (!Guid.TryParse(componentId, out var guid))
                {
                    error = $"Invalid component_id '{componentId}'.";
                    return null;
                }

                // Snapshot play/edit worlds temporarily contain different component
                // instances with the same persistent ID. Resolve against the caller's
                // live node first; the global cache can legitimately point at the
                // dormant instance from the other world.
                XRComponent? component = components.FirstOrDefault(candidate => candidate.ID == guid);
                if (component is not null)
                    return component;

                if (XRObjectBase.ObjectsCache.TryGetValue(guid, out var obj) &&
                    obj is XRComponent cachedComponent &&
                    ReferenceEquals(cachedComponent.SceneNode, node))
                {
                    return cachedComponent;
                }

                error = $"Component '{componentId}' is not on the specified node.";
                return null;
            }

            if (!string.IsNullOrWhiteSpace(componentName))
                return components.FirstOrDefault(comp => string.Equals(comp.Name, componentName, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(componentTypeName))
            {
                // 1. Exact match via type cache (includes full names)
                if (McpToolRegistry.TryResolveComponentType(componentTypeName, out var type))
                    return components.FirstOrDefault(comp => type.IsInstanceOfType(comp));

                // 2. Case-insensitive exact type name match
                var exact = components.FirstOrDefault(comp => string.Equals(comp.GetType().Name, componentTypeName, StringComparison.OrdinalIgnoreCase));
                if (exact is not null)
                    return exact;

                // 3. Suffix-tolerant match: if caller says "BoxMesh", match "BoxMeshComponent"
                var suffixed = components.FirstOrDefault(comp =>
                    comp.GetType().Name.StartsWith(componentTypeName!, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(comp.GetType().Name, componentTypeName + "Component", StringComparison.OrdinalIgnoreCase));
                if (suffixed is not null)
                    return suffixed;

                // 4. Base-type / interface match: walk the inheritance chain for each component
                var baseMatch = components.FirstOrDefault(comp =>
                {
                    for (var t = comp.GetType(); t is not null && t != typeof(object); t = t.BaseType)
                    {
                        if (string.Equals(t.Name, componentTypeName, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(t.Name, componentTypeName + "Component", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    return false;
                });
                if (baseMatch is not null)
                    return baseMatch;

                // 5. Common alias mapping
                string? aliasedTypeName = ResolveComponentAlias(componentTypeName!);
                if (aliasedTypeName is not null)
                {
                    var aliased = components.FirstOrDefault(comp =>
                    {
                        for (var t = comp.GetType(); t is not null && t != typeof(object); t = t.BaseType)
                        {
                            if (string.Equals(t.Name, aliasedTypeName, StringComparison.OrdinalIgnoreCase))
                                return true;
                        }
                        return false;
                    });
                    if (aliased is not null)
                        return aliased;
                }

                // 6. Substring match as last resort
                var substring = components.FirstOrDefault(comp =>
                    comp.GetType().Name.Contains(componentTypeName!, StringComparison.OrdinalIgnoreCase));
                if (substring is not null)
                    return substring;

                error = $"No component matching type '{componentTypeName}' found on node '{node.Name}'. "
                    + $"Available components: [{string.Join(", ", components.Select(c => c.GetType().Name))}].";
                return null;
            }

            return null;
        }

        /// <summary>
        /// Maps common external/Unity-style names to XREngine component type names.
        /// </summary>
        private static string? ResolveComponentAlias(string name)
        {
            return name.ToLowerInvariant() switch
            {
                "meshrenderer" or "mesh_renderer" or "renderer" => "RenderableComponent",
                "meshfilter" or "mesh_filter" or "mesh" => "ModelComponent",
                "model" => "ModelComponent",
                "boxcollider" or "box_collider" => "BoxMeshComponent",
                "spherecollider" or "sphere_collider" => "SphereMeshComponent",
                "light" or "pointlight" or "point_light" => "LightComponent",
                "directionallight" or "directional_light" => "DirectionalLightComponent",
                "spotlight" or "spot_light" => "SpotLightComponent",
                "camera" => "CameraComponent",
                "rigidbody" or "rigid_body" => "RigidBodyComponent",
                "transform" => "TransformComponent",
                "audio" or "audiosource" or "audio_source" => "AudioSourceComponent",
                _ => null
            };
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

        private static object ToMcpVector2(Vector2 value)
            => new { x = value.X, y = value.Y };

        private static object ToMcpVector2(IVector2 value)
            => new { x = value.X, y = value.Y };

        private static object ToMcpVector3(Vector3 value)
            => new { x = value.X, y = value.Y, z = value.Z };

        private static object ToMcpQuaternion(Quaternion value)
            => new { x = value.X, y = value.Y, z = value.Z, w = value.W };

        private static object ToMcpRectangle(BoundingRectangle value)
            => new
            {
                x = value.X,
                y = value.Y,
                width = value.Width,
                height = value.Height,
                localOriginPercentage = ToMcpVector2(value.LocalOriginPercentage)
            };

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
