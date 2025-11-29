using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using XREngine;
using XREngine.Diagnostics;
using XREngine.Rendering;
using XREngine.Scene.Transforms;

namespace XREngine.Scene.Prefabs
{
    /// <summary>
    /// Helper utilities for stamping prefab metadata onto scene nodes and cloning prefab hierarchies.
    /// </summary>
    public static class SceneNodePrefabUtility
    {
        /// <summary>
        /// Ensures every node within the hierarchy owns prefab metadata so instances can keep a stable GUID per template node.
        /// </summary>
        public static void EnsurePrefabMetadata(SceneNode root, Guid prefabAssetId, bool overwriteExisting = true)
        {
            ArgumentNullException.ThrowIfNull(root);

            foreach (SceneNode node in EnumerateHierarchy(root))
            {
                node.Prefab ??= new SceneNodePrefabLink();
                var link = node.Prefab;

                if (overwriteExisting || link.PrefabNodeId == Guid.Empty)
                    link.PrefabNodeId = Guid.NewGuid();

                if (overwriteExisting || link.PrefabAssetId == Guid.Empty)
                    link.PrefabAssetId = prefabAssetId;

                if (ReferenceEquals(node, root))
                    link.IsPrefabRoot = true;
                else if (overwriteExisting)
                    link.IsPrefabRoot = false;
            }
        }

        /// <summary>
        /// Removes prefab metadata from the provided hierarchy. Useful when breaking an instance link in the editor.
        /// </summary>
        public static void ClearPrefabMetadata(SceneNode root)
        {
            ArgumentNullException.ThrowIfNull(root);

            foreach (SceneNode node in EnumerateHierarchy(root))
                node.Prefab = null;
        }

        /// <summary>
        /// Creates a deep clone of the hierarchy rooted at <paramref name="template"/> using the engine serializers.
        /// </summary>
        public static SceneNode CloneHierarchy(SceneNode template)
        {
            ArgumentNullException.ThrowIfNull(template);

            string serialized = AssetManager.Serializer.Serialize(template);
            var clone = AssetManager.Deserializer.Deserialize<SceneNode>(serialized)
                ?? throw new InvalidOperationException("Failed to deserialize prefab hierarchy");

            DetachWorldRecursive(clone);
            return clone;
        }

        /// <summary>
        /// Creates a runtime-ready instance of the prefab, optionally attaching it to a parent node and world.
        /// </summary>
        public static SceneNode Instantiate(SceneNode template,
                                            Guid prefabAssetId,
                                            XRWorldInstance? world = null,
                                            SceneNode? parent = null,
                                            bool maintainWorldTransform = false)
        {
            var clone = CloneHierarchy(template);
            BindInstanceToPrefab(clone, prefabAssetId);

            if (parent is not null)
            {
                if (maintainWorldTransform)
                    clone.Transform.SetParent(parent.Transform, true, now: true);
                else
                    clone.Parent = parent;
            }

            if (world is not null)
                ApplyWorldRecursive(clone, world);

            return clone;
        }

        /// <summary>
        /// Applies prefab metadata to an instantiated hierarchy, ensuring every node references the correct asset ID.
        /// </summary>
        public static void BindInstanceToPrefab(SceneNode instanceRoot, Guid prefabAssetId)
        {
            ArgumentNullException.ThrowIfNull(instanceRoot);

            foreach (SceneNode node in EnumerateHierarchy(instanceRoot))
            {
                node.Prefab ??= new SceneNodePrefabLink();
                node.Prefab.PrefabAssetId = prefabAssetId;

                if (node.Prefab.PrefabNodeId == Guid.Empty)
                    node.Prefab.PrefabNodeId = Guid.NewGuid();

                node.Prefab.IsPrefabRoot = ReferenceEquals(node, instanceRoot);
            }
        }

        /// <summary>
        /// Records or updates a property override on the given prefab-linked node.
        /// </summary>
        public static void RecordPropertyOverride(SceneNode node, string propertyPath, object? value)
        {
            ArgumentNullException.ThrowIfNull(node);
            ArgumentException.ThrowIfNullOrWhiteSpace(propertyPath);

            node.Prefab ??= new SceneNodePrefabLink();
            if (node.Prefab.PrefabNodeId == Guid.Empty)
                node.Prefab.PrefabNodeId = Guid.NewGuid();

            string serialized = value is null ? string.Empty : AssetManager.Serializer.Serialize(value);
            node.Prefab.PropertyOverrides[propertyPath] = new SceneNodePrefabPropertyOverride
            {
                PropertyPath = propertyPath,
                SerializedValue = serialized,
                SerializedType = value?.GetType()?.AssemblyQualifiedName
            };
        }

        /// <summary>
        /// Removes a recorded property override from the node.
        /// </summary>
        public static bool RemovePropertyOverride(SceneNode node, string propertyPath)
        {
            ArgumentNullException.ThrowIfNull(node);
            ArgumentException.ThrowIfNullOrWhiteSpace(propertyPath);

            if (node.Prefab?.PropertyOverrides is not { Count: > 0 } overrides)
                return false;

            bool removed = overrides.Remove(propertyPath);
            return removed;
        }

        /// <summary>
        /// Extracts all overrides stored on the instance hierarchy.
        /// </summary>
        public static List<SceneNodePrefabNodeOverride> ExtractOverrides(SceneNode instanceRoot)
        {
            ArgumentNullException.ThrowIfNull(instanceRoot);

            List<SceneNodePrefabNodeOverride> overrides = [];
            foreach (SceneNode node in EnumerateHierarchy(instanceRoot))
                if (BuildNodeOverrideSnapshot(node) is SceneNodePrefabNodeOverride snapshot)
                    overrides.Add(snapshot);

            return overrides;
        }

        /// <summary>
        /// True if the node currently stores any overrides.
        /// </summary>
        public static bool HasOverrides(SceneNode node)
            => node.Prefab?.PropertyOverrides.Count > 0;

        /// <summary>
        /// Builds a lookup table mapping prefab node IDs to the instantiated scene nodes.
        /// </summary>
        public static Dictionary<Guid, SceneNode> BuildPrefabNodeMap(SceneNode instanceRoot)
        {
            ArgumentNullException.ThrowIfNull(instanceRoot);

            Dictionary<Guid, SceneNode> map = new();
            foreach (SceneNode node in EnumerateHierarchy(instanceRoot))
            {
                Guid nodeId = node.Prefab?.PrefabNodeId ?? Guid.Empty;
                if (nodeId != Guid.Empty)
                    map[nodeId] = node;
            }

            return map;
        }

        /// <summary>
        /// Finds a descendant node by its prefab GUID.
        /// </summary>
        public static SceneNode? FindNodeByPrefabId(SceneNode instanceRoot, Guid prefabNodeId)
        {
            if (prefabNodeId == Guid.Empty)
                return null;

            foreach (SceneNode node in EnumerateHierarchy(instanceRoot))
                if (node.Prefab?.PrefabNodeId == prefabNodeId)
                    return node;

            return null;
        }

        /// <summary>
        /// Applies serialized prefab overrides to the provided instance hierarchy.
        /// </summary>
        [RequiresUnreferencedCode("Prefab override reflection requires runtime metadata.")]
        public static void ApplyOverrides(SceneNode instanceRoot, IEnumerable<SceneNodePrefabNodeOverride>? nodeOverrides)
        {
            ArgumentNullException.ThrowIfNull(instanceRoot);

            if (nodeOverrides is null)
                return;

            var map = BuildPrefabNodeMap(instanceRoot);
            foreach (SceneNodePrefabNodeOverride? overrideEntry in nodeOverrides)
            {
                if (overrideEntry is null || overrideEntry.PrefabNodeId == Guid.Empty)
                    continue;

                if (!map.TryGetValue(overrideEntry.PrefabNodeId, out SceneNode? targetNode))
                {
                    Debug.LogWarning($"Prefab override references missing node {overrideEntry.PrefabNodeId}");
                    continue;
                }

                foreach (SceneNodePrefabPropertyOverride overrideData in overrideEntry.Properties.Values)
                    TryApplyPropertyOverride(targetNode, overrideData);
            }
        }

        /// <summary>
        /// Enumerates the node hierarchy depth-first.
        /// </summary>
        public static IEnumerable<SceneNode> EnumerateHierarchy(SceneNode root)
        {
            ArgumentNullException.ThrowIfNull(root);

            yield return root;

            foreach (SceneNode child in EnumerateChildren(root))
                foreach (SceneNode descendant in EnumerateHierarchy(child))
                    yield return descendant;
        }

        private static IEnumerable<SceneNode> EnumerateChildren(SceneNode node)
        {
            foreach (TransformBase? child in node.Transform.Children)
                if (child?.SceneNode is SceneNode childNode)
                    yield return childNode;
        }

        private static void DetachWorldRecursive(SceneNode node)
        {
            node.World = null;
            foreach (var component in node.Components)
                component.World = null;

            foreach (SceneNode child in EnumerateChildren(node))
                DetachWorldRecursive(child);
        }

        private static void ApplyWorldRecursive(SceneNode node, XRWorldInstance world)
        {
            node.World = world;
            foreach (var component in node.Components)
                component.World = world;

            foreach (SceneNode child in EnumerateChildren(node))
                ApplyWorldRecursive(child, world);
        }

        [RequiresUnreferencedCode("Prefab override reflection requires runtime metadata.")]
        private static bool TryApplyPropertyOverride(SceneNode targetNode, SceneNodePrefabPropertyOverride overrideData)
        {
            if (targetNode is null || overrideData is null)
                return false;

            if (string.IsNullOrWhiteSpace(overrideData.PropertyPath))
                return false;

            string[] segments = overrideData.PropertyPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
                return false;

            object? owner = targetNode;
            PropertyDescriptor? descriptor = null;

            for (int i = 0; i < segments.Length; i++)
            {
                if (owner is null)
                {
                    Debug.LogWarning($"Prefab override '{overrideData.PropertyPath}' encountered a null owner at segment '{segments[i]}'");
                    return false;
                }

                descriptor = TypeDescriptor.GetProperties(owner).Find(segments[i], false);
                if (descriptor is null)
                {
                    Debug.LogWarning($"Prefab override '{overrideData.PropertyPath}' target property missing on type '{owner.GetType().Name}'");
                    return false;
                }

                if (i == segments.Length - 1)
                    break;

                owner = descriptor.GetValue(owner);
            }

            if (descriptor is null || owner is null)
                return false;

            object? value = DeserializeOverrideValue(descriptor.PropertyType, overrideData);
            try
            {
                descriptor.SetValue(owner, value);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to apply prefab override '{overrideData.PropertyPath}': {ex.Message}");
                return false;
            }
        }

        [RequiresUnreferencedCode("Prefab override reflection requires runtime metadata.")]
        private static object? DeserializeOverrideValue(Type targetType, SceneNodePrefabPropertyOverride overrideData)
        {
            if (string.IsNullOrWhiteSpace(overrideData.SerializedValue))
                return null;

            Type resolvedType = ResolveOverrideType(targetType, overrideData.SerializedType);

            try
            {
                using var reader = new StringReader(overrideData.SerializedValue);
                return AssetManager.Deserializer.Deserialize(reader, resolvedType);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to deserialize prefab override for '{overrideData.PropertyPath}': {ex.Message}");
                return null;
            }
        }

        [RequiresUnreferencedCode("Prefab override reflection requires runtime metadata.")]
        private static Type ResolveOverrideType(Type fallback, string? serializedType)
        {
            if (string.IsNullOrWhiteSpace(serializedType))
                return fallback;

            Type? hinted = Type.GetType(serializedType, throwOnError: false);
            if (hinted is null)
                return fallback;

            if (fallback.IsAssignableFrom(hinted))
                return hinted;

            return hinted;
        }

        private static SceneNodePrefabNodeOverride? BuildNodeOverrideSnapshot(SceneNode node)
        {
            var link = node.Prefab;
            if (link is null || link.PrefabNodeId == Guid.Empty || link.PropertyOverrides.Count == 0)
                return null;

            SceneNodePrefabNodeOverride snapshot = new()
            {
                PrefabNodeId = link.PrefabNodeId,
                Properties = new Dictionary<string, SceneNodePrefabPropertyOverride>(link.PropertyOverrides.Count, StringComparer.Ordinal)
            };

            foreach (var pair in link.PropertyOverrides)
                snapshot.Properties[pair.Key] = CloneOverride(pair.Value);

            return snapshot;
        }

        private static SceneNodePrefabPropertyOverride CloneOverride(SceneNodePrefabPropertyOverride source)
            => new()
            {
                PropertyPath = source.PropertyPath,
                SerializedValue = source.SerializedValue,
                SerializedType = source.SerializedType
            };
    }
}
