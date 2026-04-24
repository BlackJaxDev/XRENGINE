using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Numerics;
using XREngine.Components;
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
        public delegate bool PrefabPropertyOverrideHandler(SceneNode targetNode, SceneNodePrefabPropertyOverride overrideData);
        public delegate TransformBase PrefabTransformCloneHandler(TransformBase sourceTransform);
        public delegate XRComponent? PrefabComponentCloneHandler(XRComponent sourceComponent, SceneNode targetNode);

        private static readonly Dictionary<string, PrefabPropertyOverrideHandler> PropertyOverrideHandlers = new(StringComparer.Ordinal);
        private static readonly Dictionary<Type, PrefabTransformCloneHandler> TransformCloneHandlers = new();
        private static readonly Dictionary<Type, PrefabComponentCloneHandler> ComponentCloneHandlers = new();

        static SceneNodePrefabUtility()
        {
            RegisterBuiltInPropertyOverrideHandlers();
            RegisterTransformCloneHandler<Transform>(
                static source => new Transform(source.Scale, source.Translation, source.Rotation, order: source.Order)
                {
                    Name = source.Name
                });
        }

        public static void RegisterPropertyOverrideHandler(string propertyPath, PrefabPropertyOverrideHandler handler)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(propertyPath);
            ArgumentNullException.ThrowIfNull(handler);

            PropertyOverrideHandlers[propertyPath] = handler;
        }

        public static void RegisterTransformCloneHandler<TTransform>(Func<TTransform, TTransform> handler)
            where TTransform : TransformBase
        {
            ArgumentNullException.ThrowIfNull(handler);
            RegisterTransformCloneHandler(typeof(TTransform), source => handler((TTransform)source));
        }

        public static void RegisterTransformCloneHandler(Type transformType, PrefabTransformCloneHandler handler)
        {
            ArgumentNullException.ThrowIfNull(transformType);
            ArgumentNullException.ThrowIfNull(handler);

            if (!typeof(TransformBase).IsAssignableFrom(transformType))
                throw new ArgumentException($"Type must derive from {nameof(TransformBase)}.", nameof(transformType));

            TransformCloneHandlers[transformType] = handler;
        }

        public static void RegisterComponentCloneHandler<TComponent>(Func<TComponent, SceneNode, TComponent?> handler)
            where TComponent : XRComponent
        {
            ArgumentNullException.ThrowIfNull(handler);
            RegisterComponentCloneHandler(typeof(TComponent), (source, target) => handler((TComponent)source, target));
        }

        public static void RegisterComponentCloneHandler(Type componentType, PrefabComponentCloneHandler handler)
        {
            ArgumentNullException.ThrowIfNull(componentType);
            ArgumentNullException.ThrowIfNull(handler);

            if (!typeof(XRComponent).IsAssignableFrom(componentType))
                throw new ArgumentException($"Type must derive from {nameof(XRComponent)}.", nameof(componentType));

            ComponentCloneHandlers[componentType] = handler;
        }

        public static bool TryDeserializeOverrideValue<T>(SceneNodePrefabPropertyOverride overrideData, out T? value)
        {
            ArgumentNullException.ThrowIfNull(overrideData);

            value = default;
            if (string.IsNullOrWhiteSpace(overrideData.SerializedValue))
                return true;

            if (XRRuntimeEnvironment.IsAotRuntimeBuild)
            {
                if (TryDeserializeAotOverrideValue(typeof(T), overrideData.SerializedValue, out object? parsed))
                {
                    value = parsed is null ? default : (T)parsed;
                    return true;
                }

                Debug.LogWarning($"Failed to deserialize prefab override for '{overrideData.PropertyPath}' in published AOT mode: no bounded parser for '{typeof(T).FullName}'.");
                return false;
            }

            try
            {
                using var reader = new StringReader(overrideData.SerializedValue);
                value = AssetManager.Deserializer.Deserialize<T>(reader);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to deserialize prefab override for '{overrideData.PropertyPath}': {ex.Message}");
                return false;
            }
        }

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

            if (XRRuntimeEnvironment.IsAotRuntimeBuild)
                return CloneHierarchyBounded(template);

            string serialized = AssetManager.Serializer.Serialize(template);
            var clone = AssetManager.Deserializer.Deserialize<SceneNode>(serialized)
                ?? throw new InvalidOperationException("Failed to deserialize prefab hierarchy");

            DetachWorldRecursive(clone);
            NotifyYamlHierarchyDeserialized(clone);
            return clone;
        }

        /// <summary>
        /// Mirrors the cooked-binary <c>IPostCookedBinaryDeserialize</c> hook for YAML-deserialized
        /// hierarchies. YamlDotNet reads <c>Components</c> (Order=0) before <c>Transform</c> and does
        /// not invoke <c>NotifyOwningSceneNodePostDeserialize</c> itself, which leaves components
        /// that build runtime state from transform-dependent data (e.g. <see cref="Mesh.ModelComponent"/>
        /// rebuilding <c>RenderableMesh</c> instances) with their rebuild deferred and never retried.
        /// Running this pass once the full tree is assembled lets each component resolve its sibling
        /// transforms and rebind serialized bone references against the completed hierarchy.
        /// </summary>
        internal static void NotifyYamlHierarchyDeserialized(SceneNode root)
        {
            ArgumentNullException.ThrowIfNull(root);

            foreach (SceneNode node in EnumerateHierarchy(root))
                foreach (XREngine.Components.XRComponent component in node.Components)
                    component.NotifyOwningSceneNodePostDeserialize();
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
                    clone.Transform.SetParent(parent.Transform, true, EParentAssignmentMode.Immediate);
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

        private static SceneNode CloneHierarchyBounded(SceneNode template)
        {
            TransformBase clonedTransform = CloneTransformBounded(template.Transform);
            SceneNode clone = new(template.Name ?? SceneNode.DefaultName, clonedTransform)
            {
                IsActiveSelf = template.IsActiveSelf,
                IsEditorOnly = template.IsEditorOnly,
                Layer = template.Layer,
                Prefab = ClonePrefabLink(template.Prefab)
            };

            foreach (XRComponent component in template.Components)
            {
                if (!TryCloneComponentBounded(component, clone))
                {
                    throw new InvalidOperationException(
                        $"Prefab node '{template.Name}' contains component '{component.GetType().FullName}' without a registered AOT clone handler. " +
                        $"Register one with {nameof(RegisterComponentCloneHandler)} before instantiating this prefab in published AOT builds.");
                }
            }

            foreach (SceneNode child in EnumerateChildren(template))
            {
                SceneNode childClone = CloneHierarchyBounded(child);
                childClone.Parent = clone;
            }

            NotifyYamlHierarchyDeserialized(clone);
            return clone;
        }

        private static TransformBase CloneTransformBounded(TransformBase source)
        {
            Type sourceType = source.GetType();
            if (TryGetTransformCloneHandler(sourceType, out PrefabTransformCloneHandler? handler) && handler is not null)
                return handler(source);

            throw new InvalidOperationException(
                $"Prefab transform '{sourceType.FullName}' has no registered AOT clone handler. " +
                $"Register one with {nameof(RegisterTransformCloneHandler)} before instantiating this prefab in published AOT builds.");
        }

        private static bool TryGetTransformCloneHandler(Type transformType, out PrefabTransformCloneHandler? handler)
        {
            if (TransformCloneHandlers.TryGetValue(transformType, out handler))
                return true;

            foreach (var pair in TransformCloneHandlers)
            {
                if (pair.Key.IsAssignableFrom(transformType))
                {
                    handler = pair.Value;
                    return true;
                }
            }

            handler = null;
            return false;
        }

        private static bool TryCloneComponentBounded(XRComponent source, SceneNode targetNode)
        {
            Type sourceType = source.GetType();
            if (!TryGetComponentCloneHandler(sourceType, out PrefabComponentCloneHandler? handler) || handler is null)
                return false;

            XRComponent? clone = handler(source, targetNode);
            return clone is not null;
        }

        private static bool TryGetComponentCloneHandler(Type componentType, out PrefabComponentCloneHandler? handler)
        {
            if (ComponentCloneHandlers.TryGetValue(componentType, out handler))
                return true;

            foreach (var pair in ComponentCloneHandlers)
            {
                if (pair.Key.IsAssignableFrom(componentType))
                {
                    handler = pair.Value;
                    return true;
                }
            }

            handler = null;
            return false;
        }

        private static SceneNodePrefabLink? ClonePrefabLink(SceneNodePrefabLink? source)
        {
            if (source is null)
                return null;

            SceneNodePrefabLink clone = new()
            {
                PrefabAssetId = source.PrefabAssetId,
                PrefabNodeId = source.PrefabNodeId,
                IsPrefabRoot = source.IsPrefabRoot,
                PropertyOverrides = new Dictionary<string, SceneNodePrefabPropertyOverride>(source.PropertyOverrides.Count, StringComparer.Ordinal)
            };

            foreach (var pair in source.PropertyOverrides)
                clone.PropertyOverrides[pair.Key] = CloneOverride(pair.Value);

            return clone;
        }

        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The reflection fallback is guarded out of published AOT builds; AOT builds require registered property override handlers.")]
        private static bool TryApplyPropertyOverride(SceneNode targetNode, SceneNodePrefabPropertyOverride overrideData)
        {
            if (targetNode is null || overrideData is null)
                return false;

            if (string.IsNullOrWhiteSpace(overrideData.PropertyPath))
                return false;

            if (PropertyOverrideHandlers.TryGetValue(overrideData.PropertyPath, out PrefabPropertyOverrideHandler? handler))
                return handler(targetNode, overrideData);

            if (XRRuntimeEnvironment.IsAotRuntimeBuild)
                throw new InvalidOperationException($"No registered prefab property override handler for path '{overrideData.PropertyPath}'.");

            return TryApplyPropertyOverrideReflective(targetNode, overrideData);
        }

        [RequiresUnreferencedCode("Prefab override reflection requires runtime metadata.")]
        private static bool TryApplyPropertyOverrideReflective(SceneNode targetNode, SceneNodePrefabPropertyOverride overrideData)
        {
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

        private static void RegisterBuiltInPropertyOverrideHandlers()
        {
            RegisterPropertyOverrideHandler(nameof(SceneNode.Name), static (node, data) =>
            {
                if (!TryDeserializeOverrideValue<string>(data, out string? value))
                    return false;

                node.Name = value;
                return true;
            });

            RegisterPropertyOverrideHandler(nameof(SceneNode.IsActiveSelf), static (node, data) =>
            {
                if (!TryDeserializeOverrideValue<bool>(data, out bool value))
                    return false;

                node.IsActiveSelf = value;
                return true;
            });

            RegisterPropertyOverrideHandler(nameof(SceneNode.IsEditorOnly), static (node, data) =>
            {
                if (!TryDeserializeOverrideValue<bool>(data, out bool value))
                    return false;

                node.IsEditorOnly = value;
                return true;
            });

            RegisterPropertyOverrideHandler(nameof(SceneNode.Layer), static (node, data) =>
            {
                if (!TryDeserializeOverrideValue<int>(data, out int value))
                    return false;

                node.Layer = value;
                return true;
            });

            RegisterPropertyOverrideHandler($"{nameof(SceneNode.Transform)}.{nameof(Transform.Translation)}", ApplyTransformTranslationOverride);
            RegisterPropertyOverrideHandler("Transform.Position", ApplyTransformTranslationOverride);
            RegisterPropertyOverrideHandler($"{nameof(SceneNode.Transform)}.{nameof(Transform.Scale)}", static (node, data) =>
            {
                if (node.Transform is not Transform transform || !TryDeserializeOverrideValue<Vector3>(data, out Vector3 value))
                    return false;

                transform.Scale = value;
                return true;
            });
            RegisterPropertyOverrideHandler($"{nameof(SceneNode.Transform)}.{nameof(Transform.Rotation)}", static (node, data) =>
            {
                if (node.Transform is not Transform transform || !TryDeserializeOverrideValue<Quaternion>(data, out Quaternion value))
                    return false;

                transform.Rotation = value;
                return true;
            });
        }

        private static bool ApplyTransformTranslationOverride(SceneNode node, SceneNodePrefabPropertyOverride data)
        {
            if (node.Transform is not Transform transform || !TryDeserializeOverrideValue<Vector3>(data, out Vector3 value))
                return false;

            transform.Translation = value;
            return true;
        }

        private static bool TryDeserializeAotOverrideValue(Type targetType, string serializedValue, out object? value)
        {
            value = null;
            Type effectiveType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            string scalar = NormalizeSerializedScalar(serializedValue);

            if (effectiveType == typeof(string))
            {
                value = scalar;
                return true;
            }

            if (string.Equals(scalar, "null", StringComparison.OrdinalIgnoreCase) || scalar == "~")
                return !effectiveType.IsValueType;

            if (effectiveType == typeof(bool) && bool.TryParse(scalar, out bool boolValue))
            {
                value = boolValue;
                return true;
            }

            if (effectiveType == typeof(int) && int.TryParse(scalar, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intValue))
            {
                value = intValue;
                return true;
            }

            if (effectiveType == typeof(float) && float.TryParse(scalar, NumberStyles.Float, CultureInfo.InvariantCulture, out float floatValue))
            {
                value = floatValue;
                return true;
            }

            if (effectiveType == typeof(Guid) && Guid.TryParse(scalar, out Guid guidValue))
            {
                value = guidValue;
                return true;
            }

            if (effectiveType.IsEnum)
            {
                if (long.TryParse(scalar, NumberStyles.Integer, CultureInfo.InvariantCulture, out long enumNumber))
                {
                    value = Enum.ToObject(effectiveType, enumNumber);
                    return true;
                }

                try
                {
                    value = Enum.Parse(effectiveType, scalar, ignoreCase: true);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            Dictionary<string, string> map = ParseYamlLikeMap(serializedValue);
            if (effectiveType == typeof(Vector2) && TryReadFloat(map, "X", out float x2) && TryReadFloat(map, "Y", out float y2))
            {
                value = new Vector2(x2, y2);
                return true;
            }

            if (effectiveType == typeof(Vector3) &&
                TryReadFloat(map, "X", out float x3) &&
                TryReadFloat(map, "Y", out float y3) &&
                TryReadFloat(map, "Z", out float z3))
            {
                value = new Vector3(x3, y3, z3);
                return true;
            }

            if (effectiveType == typeof(Vector4) &&
                TryReadFloat(map, "X", out float x4) &&
                TryReadFloat(map, "Y", out float y4) &&
                TryReadFloat(map, "Z", out float z4) &&
                TryReadFloat(map, "W", out float w4))
            {
                value = new Vector4(x4, y4, z4, w4);
                return true;
            }

            if (effectiveType == typeof(Quaternion) &&
                TryReadFloat(map, "X", out float qx) &&
                TryReadFloat(map, "Y", out float qy) &&
                TryReadFloat(map, "Z", out float qz) &&
                TryReadFloat(map, "W", out float qw))
            {
                value = new Quaternion(qx, qy, qz, qw);
                return true;
            }

            return false;
        }

        private static string NormalizeSerializedScalar(string serializedValue)
        {
            string[] lines = serializedValue
                .Split(["\r\n", "\n"], StringSplitOptions.None)
                .Select(static line => line.Trim())
                .Where(static line => line.Length > 0 && line != "---" && line != "...")
                .ToArray();

            string value = lines.Length == 0 ? string.Empty : string.Join(" ", lines);
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            return value;
        }

        private static Dictionary<string, string> ParseYamlLikeMap(string serializedValue)
        {
            Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
            string trimmed = serializedValue.Trim();

            if (trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal))
                trimmed = trimmed[1..^1];
            else if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
                trimmed = trimmed[1..^1];

            foreach (string rawPart in trimmed.Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string part = rawPart;
                if (part is "---" or "...")
                    continue;

                int separator = part.IndexOf(':');
                if (separator < 0)
                    continue;

                string key = part[..separator].Trim().Trim('"', '\'');
                string value = part[(separator + 1)..].Trim().Trim('"', '\'');
                if (key.Length > 0)
                    map[key] = value;
            }

            return map;
        }

        private static bool TryReadFloat(Dictionary<string, string> map, string key, out float value)
        {
            value = 0.0f;
            return map.TryGetValue(key, out string? raw) &&
                float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
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

        // ────────── High-level helpers (merged from SceneNodePrefabService) ──────────

        /// <summary>
        /// Creates a prefab asset from the supplied hierarchy and saves it to the target directory.
        /// </summary>
        public static XRPrefabSource CreatePrefabAsset(SceneNode sourceRoot, string assetName, string targetDirectory)
        {
            ArgumentNullException.ThrowIfNull(sourceRoot);
            ArgumentException.ThrowIfNullOrWhiteSpace(assetName);
            ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

            var clone = CloneHierarchy(sourceRoot);
            XRPrefabSource prefab = new()
            {
                Name = assetName,
                RootNode = clone
            };
            EnsurePrefabMetadata(prefab.RootNode!, prefab.ID, overwriteExisting: true);

            Engine.Assets.SaveTo(prefab, targetDirectory);
            return prefab;
        }

        /// <summary>
        /// Creates a prefab variant asset from the supplied instance hierarchy and base prefab asset.
        /// </summary>
        public static XRPrefabVariant CreateVariantAsset(SceneNode instanceRoot,
                                                          XRPrefabSource basePrefab,
                                                          string assetName,
                                                          string targetDirectory)
        {
            ArgumentNullException.ThrowIfNull(instanceRoot);
            ArgumentNullException.ThrowIfNull(basePrefab);
            ArgumentException.ThrowIfNullOrWhiteSpace(assetName);
            ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

            List<SceneNodePrefabNodeOverride> overrides = ExtractOverrides(instanceRoot);

            XRPrefabVariant variant = new()
            {
                Name = assetName,
                BasePrefab = basePrefab,
                NodeOverrides = overrides
            };

            Engine.Assets.SaveTo(variant, targetDirectory);
            return variant;
        }

        /// <summary>
        /// Instantiates the given prefab source into the world/parent provided.
        /// </summary>
        public static SceneNode Instantiate(XRPrefabSource prefab,
                                            XRWorldInstance? world = null,
                                            SceneNode? parent = null,
                                            bool maintainWorldTransform = false)
        {
            ArgumentNullException.ThrowIfNull(prefab);
            return prefab.Instantiate(world, parent, maintainWorldTransform);
        }

        /// <summary>
        /// Instantiates a prefab variant by cloning the base prefab and replaying its overrides.
        /// </summary>
        public static SceneNode InstantiateVariant(XRPrefabVariant variant,
                                                   XRWorldInstance? world = null,
                                                   SceneNode? parent = null,
                                                   bool maintainWorldTransform = false)
        {
            ArgumentNullException.ThrowIfNull(variant);
            return variant.Instantiate(world, parent, maintainWorldTransform);
        }

        /// <summary>
        /// Generates a snapshot of overrides from the instance hierarchy for serialization.
        /// </summary>
        public static List<SceneNodePrefabNodeOverride> CaptureOverrides(SceneNode instanceRoot)
        {
            ArgumentNullException.ThrowIfNull(instanceRoot);
            return ExtractOverrides(instanceRoot);
        }

        /// <summary>
        /// Applies the overrides stored on the variant to an existing instance hierarchy (useful when refreshing changes).
        /// </summary>
        public static void ApplyVariantOverrides(SceneNode instanceRoot, XRPrefabVariant variant)
        {
            ArgumentNullException.ThrowIfNull(instanceRoot);
            ArgumentNullException.ThrowIfNull(variant);

            ApplyOverrides(instanceRoot, variant.NodeOverrides);
        }

        /// <summary>
        /// Removes prefab metadata from the provided hierarchy, effectively breaking the link to its source prefab.
        /// </summary>
        public static void BreakPrefabLink(SceneNode instanceRoot)
        {
            ArgumentNullException.ThrowIfNull(instanceRoot);
            ClearPrefabMetadata(instanceRoot);
        }
    }
}
