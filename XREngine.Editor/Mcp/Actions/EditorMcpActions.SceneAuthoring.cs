using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using XREngine.Components;
using XREngine.Components.Scene.Transforms;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Data.Transforms.Rotations;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.Editor.Mcp
{
    public sealed partial class EditorMcpActions
    {
        // ═══════════════════════════════════════════════════════════════════
        // Phase 6 — Advanced Scene Authoring Workflows
        // ═══════════════════════════════════════════════════════════════════

        // ───────────────────────────────────────────────────────────────────
        // P6.1 — Prefab Operations
        // ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Instantiates a <see cref="XRPrefabSource"/> into the active scene.
        /// The prefab can be resolved by asset GUID or by a file path relative to game assets.
        /// </summary>
        [XRMcp(Name = "instantiate_prefab", Permission = McpPermissionLevel.Mutate, PermissionReason = "Creates new scene nodes from a prefab template.")]
        [Description("Instantiate a prefab into the active scene by asset ID or path.")]
        public static Task<McpToolResponse> InstantiatePrefabAsync(
            McpToolContext context,
            [McpName("prefab_id"), Description("GUID of the XRPrefabSource asset. Provide this or prefab_path.")]
            string? prefabId = null,
            [McpName("prefab_path"), Description("Relative path to the prefab asset within game assets. Provide this or prefab_id.")]
            string? prefabPath = null,
            [McpName("parent_id"), Description("Optional parent scene node GUID to attach the instance under.")]
            string? parentId = null,
            [McpName("name"), Description("Optional name override for the instantiated root node.")]
            string? name = null,
            [McpName("position_x"), Description("World position X.")] float? positionX = null,
            [McpName("position_y"), Description("World position Y.")] float? positionY = null,
            [McpName("position_z"), Description("World position Z.")] float? positionZ = null,
            [McpName("pitch"), Description("Rotation pitch in degrees.")] float? pitch = null,
            [McpName("yaw"), Description("Rotation yaw in degrees.")] float? yaw = null,
            [McpName("roll"), Description("Rotation roll in degrees.")] float? roll = null,
            [McpName("scale_x"), Description("Scale X.")] float? scaleX = null,
            [McpName("scale_y"), Description("Scale Y.")] float? scaleY = null,
            [McpName("scale_z"), Description("Scale Z.")] float? scaleZ = null,
            [McpName("scene_name"), Description("Optional target scene name; defaults to the first active scene.")]
            string? sceneName = null)
        {
            var assets = Engine.Assets;
            if (assets is null)
                return Task.FromResult(new McpToolResponse("Asset manager is unavailable.", isError: true));

            if (string.IsNullOrWhiteSpace(prefabId) && string.IsNullOrWhiteSpace(prefabPath))
                return Task.FromResult(new McpToolResponse("Provide either prefab_id or prefab_path.", isError: true));

            // Resolve the prefab asset
            XRPrefabSource? prefab = null;

            if (!string.IsNullOrWhiteSpace(prefabId) && Guid.TryParse(prefabId, out Guid guid))
                prefab = assets.GetAssetByID(guid) as XRPrefabSource;

            if (prefab is null && !string.IsNullOrWhiteSpace(prefabPath))
            {
                if (!TryGetGameAssetsPath(out string assetsPath, out var assetsError))
                    return Task.FromResult(assetsError!);

                string fullPath = ResolveAndValidateGamePath(assetsPath, prefabPath, out var pathError, mustExist: true, expectDirectory: false);
                if (pathError is not null)
                    return Task.FromResult(pathError);

                prefab = assets.Load<XRPrefabSource>(fullPath);
            }

            if (prefab is null)
                return Task.FromResult(new McpToolResponse("Prefab asset not found.", isError: true));

            if (prefab.RootNode is null)
                return Task.FromResult(new McpToolResponse("Prefab has no root node.", isError: true));

            // Resolve parent
            SceneNode? parent = null;
            XRScene? scene = null;
            if (!string.IsNullOrWhiteSpace(parentId))
            {
                if (!TryGetNodeById(context.WorldInstance, parentId!, out parent, out var parentError) || parent is null)
                    return Task.FromResult(new McpToolResponse(parentError ?? "Parent node not found.", isError: true));
            }
            else
            {
                scene = ResolveScene(context.WorldInstance, sceneName);
                if (scene is null)
                    return Task.FromResult(new McpToolResponse("No active scene found.", isError: true));
            }

            // Instantiate
            SceneNode instance;
            try
            {
                instance = prefab.Instantiate(context.WorldInstance, parent);
            }
            catch (Exception ex)
            {
                return Task.FromResult(new McpToolResponse($"Failed to instantiate prefab: {ex.Message}", isError: true));
            }

            // Name override
            if (!string.IsNullOrWhiteSpace(name))
                instance.Name = name;

            // If no parent, add to the scene's root
            if (parent is null && scene is not null)
            {
                scene.RootNodes.Add(instance);
                if (scene.IsVisible)
                    context.WorldInstance.RootNodes.Add(instance);
            }

            // Apply transform overrides
            ApplyTransformOverrides(instance, positionX, positionY, positionZ, pitch, yaw, roll, scaleX, scaleY, scaleZ);

            // Record undo
            Undo.TrackSceneNode(instance);
            var sceneCapture = scene;
            var worldCapture = context.WorldInstance;
            var parentCapture = parent;
            using var interaction = Undo.BeginUserInteraction();
            using var scope = Undo.BeginChange($"MCP Instantiate {prefab.Name}");
            Undo.RecordStructuralChange($"Instantiate {prefab.Name}",
                undoAction: () =>
                {
                    if (parentCapture is not null)
                        parentCapture.Transform.RemoveChild(instance.Transform, EParentAssignmentMode.Immediate);
                    else if (sceneCapture is not null)
                    {
                        sceneCapture.RootNodes.Remove(instance);
                        worldCapture.RootNodes.Remove(instance);
                    }
                    instance.IsActiveSelf = false;
                },
                redoAction: () =>
                {
                    if (parentCapture is not null)
                        instance.Transform.SetParent(parentCapture.Transform, false, EParentAssignmentMode.Immediate);
                    else if (sceneCapture is not null)
                    {
                        sceneCapture.RootNodes.Add(instance);
                        if (sceneCapture.IsVisible)
                            worldCapture.RootNodes.Add(instance);
                    }
                    instance.IsActiveSelf = true;
                    Undo.TrackSceneNode(instance);
                });

            return Task.FromResult(new McpToolResponse(
                $"Instantiated prefab '{prefab.Name ?? prefab.ID.ToString()}'.",
                new
                {
                    instanceId = instance.ID,
                    instanceName = instance.Name,
                    prefabAssetId = prefab.ID,
                    prefabName = prefab.Name,
                    path = BuildNodePath(instance)
                }));
        }

        /// <summary>
        /// Saves an existing scene node hierarchy as a new <see cref="XRPrefabSource"/> asset.
        /// </summary>
        [XRMcp(Name = "create_prefab_from_node", Permission = McpPermissionLevel.Destructive, PermissionReason = "Writes a new prefab asset file to disk.")]
        [Description("Save a scene node hierarchy as a new prefab asset file.")]
        public static Task<McpToolResponse> CreatePrefabFromNodeAsync(
            McpToolContext context,
            [McpName("node_id"), Description("GUID of the root scene node to capture as a prefab.")]
            string nodeId,
            [McpName("output_path"), Description("Relative path within game assets for the prefab file (e.g., 'Prefabs/MyPrefab.asset').")]
            string outputPath,
            [McpName("name"), Description("Optional display name for the prefab asset.")]
            string? name = null)
        {
            if (!TryGetNodeById(context.WorldInstance, nodeId, out var node, out var nodeError) || node is null)
                return Task.FromResult(new McpToolResponse(nodeError ?? "Scene node not found.", isError: true));

            if (string.IsNullOrWhiteSpace(outputPath))
                return Task.FromResult(new McpToolResponse("output_path is required.", isError: true));

            if (!TryGetGameAssetsPath(out string assetsPath, out var assetsError))
                return Task.FromResult(assetsError!);

            var assets = Engine.Assets;
            if (assets is null)
                return Task.FromResult(new McpToolResponse("Asset manager is unavailable.", isError: true));

            string fullPath = ResolveAndValidateGamePath(assetsPath, outputPath, out var pathError, mustExist: false, expectDirectory: false);
            if (pathError is not null)
                return Task.FromResult(pathError);

            // Clone the hierarchy so the original is not mutated
            var clonedRoot = SceneNodePrefabUtility.CloneHierarchy(node);

            // Create the prefab asset
            var prefab = new XRPrefabSource
            {
                Name = name ?? node.Name ?? Path.GetFileNameWithoutExtension(outputPath),
                RootNode = clonedRoot
            };

            // Ensure parent directory exists
            string? dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Save
            prefab.FilePath = fullPath;
            assets.Save(prefab, bypassJobThread: true);

            return Task.FromResult(new McpToolResponse(
                $"Created prefab '{prefab.Name}' from node '{node.Name}'.",
                new
                {
                    prefabId = prefab.ID,
                    prefabName = prefab.Name,
                    filePath = fullPath,
                    sourceNodeId = node.ID,
                    sourceNodeName = node.Name
                }));
        }

        // ───────────────────────────────────────────────────────────────────
        // P6.2 — Batch Operations
        // ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates multiple scene nodes in a single round trip.
        /// Each node spec can include a name, optional parent, optional components, and optional transform.
        /// </summary>
        [XRMcp(Name = "batch_create_nodes", Permission = McpPermissionLevel.Mutate, PermissionReason = "Creates multiple scene nodes.")]
        [Description("Create multiple scene nodes in a single call. Each entry: {name, parent_id?, components?: string[], transform?: {x?,y?,z?,pitch?,yaw?,roll?,sx?,sy?,sz?}}.")]
        public static Task<McpToolResponse> BatchCreateNodesAsync(
            McpToolContext context,
            [McpName("nodes"), Description("JSON array of node specs. Each: {name: string, parent_id?: string, components?: string[], transform?: {x?,y?,z?,pitch?,yaw?,roll?,sx?,sy?,sz?}}.")]
            JsonElement nodes,
            [McpName("scene_name"), Description("Optional scene name; defaults to the first active scene.")]
            string? sceneName = null)
        {
            if (nodes.ValueKind != JsonValueKind.Array)
                return Task.FromResult(new McpToolResponse("'nodes' must be a JSON array.", isError: true));

            var world = context.WorldInstance;
            var scene = ResolveScene(world, sceneName);
            if (scene is null)
                return Task.FromResult(new McpToolResponse("No active scene found.", isError: true));

            var createdNodes = new List<object>();
            var errors = new List<string>();

            // Map of created node IDs for parent references within the same batch
            var batchNodeMap = new Dictionary<string, SceneNode>(StringComparer.OrdinalIgnoreCase);
            var sceneCapture = scene;
            var worldCapture = world;

            using var interaction = Undo.BeginUserInteraction();
            using var scope = Undo.BeginChange("MCP Batch Create Nodes");

            foreach (JsonElement nodeSpec in nodes.EnumerateArray())
            {
                string? nodeName = nodeSpec.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(nodeName))
                {
                    errors.Add("Skipped entry with missing 'name'.");
                    continue;
                }

                // Resolve parent (can reference previously-created nodes in this batch by GUID)
                SceneNode? parent = null;
                if (nodeSpec.TryGetProperty("parent_id", out var parentEl) && parentEl.ValueKind == JsonValueKind.String)
                {
                    string? parentStr = parentEl.GetString();
                    if (!string.IsNullOrWhiteSpace(parentStr))
                    {
                        // Check batch map first, then scene
                        if (!batchNodeMap.TryGetValue(parentStr, out parent))
                        {
                            if (!TryGetNodeById(world, parentStr, out parent, out _))
                            {
                                errors.Add($"Node '{nodeName}': parent '{parentStr}' not found.");
                                continue;
                            }
                        }
                    }
                }

                // Create node
                SceneNode newNode;
                if (parent is not null)
                {
                    newNode = parent.NewChild(nodeName);
                }
                else
                {
                    newNode = new SceneNode(nodeName);
                    scene.RootNodes.Add(newNode);
                    if (scene.IsVisible)
                        world.RootNodes.Add(newNode);
                }

                Undo.TrackSceneNode(newNode);
                var parentTfm = newNode.Transform.Parent;
                Undo.RecordStructuralChange($"Batch Create {newNode.Name}",
                    undoAction: () =>
                    {
                        if (parentTfm is not null)
                            parentTfm.RemoveChild(newNode.Transform, EParentAssignmentMode.Immediate);
                        else
                        {
                            sceneCapture.RootNodes.Remove(newNode);
                            worldCapture.RootNodes.Remove(newNode);
                        }
                        newNode.IsActiveSelf = false;
                    },
                    redoAction: () =>
                    {
                        if (parentTfm is not null)
                            newNode.Transform.SetParent(parentTfm, false, EParentAssignmentMode.Immediate);
                        else
                        {
                            sceneCapture.RootNodes.Add(newNode);
                            if (sceneCapture.IsVisible)
                                worldCapture.RootNodes.Add(newNode);
                        }
                        newNode.IsActiveSelf = true;
                        Undo.TrackSceneNode(newNode);
                    });

                // Add components
                if (nodeSpec.TryGetProperty("components", out var componentsEl) && componentsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement compEl in componentsEl.EnumerateArray())
                    {
                        string? compTypeName = compEl.GetString();
                        if (string.IsNullOrWhiteSpace(compTypeName))
                            continue;

                        if (McpToolRegistry.TryResolveComponentType(compTypeName, out var compType))
                            newNode.AddComponent(compType);
                        else
                            errors.Add($"Node '{nodeName}': component type '{compTypeName}' not found.");
                    }
                }

                // Apply transform
                if (nodeSpec.TryGetProperty("transform", out var tfmEl) && tfmEl.ValueKind == JsonValueKind.Object)
                {
                    ApplyTransformOverrides(newNode,
                        TryGetFloat(tfmEl, "x"), TryGetFloat(tfmEl, "y"), TryGetFloat(tfmEl, "z"),
                        TryGetFloat(tfmEl, "pitch"), TryGetFloat(tfmEl, "yaw"), TryGetFloat(tfmEl, "roll"),
                        TryGetFloat(tfmEl, "sx"), TryGetFloat(tfmEl, "sy"), TryGetFloat(tfmEl, "sz"));
                }

                // Track for subsequent parent references
                batchNodeMap[newNode.ID.ToString()] = newNode;

                createdNodes.Add(new
                {
                    id = newNode.ID,
                    name = newNode.Name,
                    path = BuildNodePath(newNode)
                });
            }

            return Task.FromResult(new McpToolResponse(
                $"Batch created {createdNodes.Count} node(s)." + (errors.Count > 0 ? $" {errors.Count} error(s)." : ""),
                new
                {
                    created = createdNodes,
                    errors = errors.Count > 0 ? errors : null
                }));
        }

        /// <summary>
        /// Sets properties across multiple components or nodes in a single call.
        /// </summary>
        [XRMcp(Name = "batch_set_properties", Permission = McpPermissionLevel.Mutate, PermissionReason = "Modifies multiple components/nodes.")]
        [Description("Set properties on multiple components/nodes in one call. Each operation: {node_id, component_type?, property_name, value}.")]
        public static Task<McpToolResponse> BatchSetPropertiesAsync(
            McpToolContext context,
            [McpName("operations"), Description("JSON array of operations. Each: {node_id: string, component_type?: string, property_name: string, value: any}.")]
            JsonElement operations)
        {
            if (operations.ValueKind != JsonValueKind.Array)
                return Task.FromResult(new McpToolResponse("'operations' must be a JSON array.", isError: true));

            var world = context.WorldInstance;
            var successes = new List<string>();
            var errors = new List<string>();

            int index = 0;
            foreach (JsonElement op in operations.EnumerateArray())
            {
                index++;
                string? nodeId = op.TryGetProperty("node_id", out var nidEl) ? nidEl.GetString() : null;
                string? componentType = op.TryGetProperty("component_type", out var ctEl) ? ctEl.GetString() : null;
                string? propertyName = op.TryGetProperty("property_name", out var pnEl) ? pnEl.GetString() : null;

                if (string.IsNullOrWhiteSpace(nodeId) || string.IsNullOrWhiteSpace(propertyName))
                {
                    errors.Add($"Op #{index}: missing node_id or property_name.");
                    continue;
                }

                if (!op.TryGetProperty("value", out var valueEl))
                {
                    errors.Add($"Op #{index}: missing value.");
                    continue;
                }

                if (!TryGetNodeById(world, nodeId!, out var node, out _) || node is null)
                {
                    errors.Add($"Op #{index}: node '{nodeId}' not found.");
                    continue;
                }

                // If component_type is provided, target a component; otherwise target the node directly
                if (!string.IsNullOrWhiteSpace(componentType))
                {
                    XRComponent? component = FindComponent(node, componentId: null, componentName: null, componentType, out var compError);
                    if (component is null)
                    {
                        errors.Add($"Op #{index}: {compError ?? $"component '{componentType}' not found on node."}");
                        continue;
                    }

                    if (TrySetComponentMember(component, propertyName!, valueEl, out string? setMessage))
                        successes.Add($"Op #{index}: {setMessage}");
                    else
                        errors.Add($"Op #{index}: {setMessage}");
                }
                else
                {
                    // Set on the node (XRBase) via reflection
                    if (TrySetNodeMember(node, propertyName!, valueEl, out string? setMessage))
                        successes.Add($"Op #{index}: {setMessage}");
                    else
                        errors.Add($"Op #{index}: {setMessage}");
                }
            }

            return Task.FromResult(new McpToolResponse(
                $"Batch set: {successes.Count} succeeded, {errors.Count} failed.",
                new
                {
                    succeeded = successes.Count,
                    failed = errors.Count,
                    successes = successes.Count > 0 ? successes : null,
                    errors = errors.Count > 0 ? errors : null
                }));
        }

        // ───────────────────────────────────────────────────────────────────
        // P6.3 — Scene Cloning
        // ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Deep-clones a scene for experimentation. The clone is added to the world but starts hidden by default.
        /// </summary>
        [XRMcp(Name = "clone_scene", Permission = McpPermissionLevel.Mutate, PermissionReason = "Creates a deep copy of a scene in the active world.")]
        [Description("Deep-clone a scene for experimentation. The clone is added to the world (hidden by default).")]
        public static Task<McpToolResponse> CloneSceneAsync(
            McpToolContext context,
            [McpName("source_scene_name"), Description("Name or GUID of the scene to clone.")]
            string sourceSceneName,
            [McpName("new_scene_name"), Description("Name for the cloned scene.")]
            string newSceneName,
            [McpName("make_visible"), Description("Whether to make the clone visible immediately.")]
            bool makeVisible = false)
        {
            var world = context.WorldInstance;
            if (!TryResolveScene(world, sourceSceneName, out var sourceScene, out var error) || sourceScene is null)
                return Task.FromResult(new McpToolResponse(error ?? "Source scene not found.", isError: true));

            var targetWorld = world.TargetWorld;
            if (targetWorld is null)
                return Task.FromResult(new McpToolResponse("No active world found.", isError: true));

            // Serialize/deserialize for deep clone
            string serialized = AssetManager.Serializer.Serialize(sourceScene);
            var clonedScene = AssetManager.Deserializer.Deserialize<XRScene>(serialized);
            if (clonedScene is null)
                return Task.FromResult(new McpToolResponse("Failed to clone the scene.", isError: true));

            clonedScene.Name = newSceneName;
            clonedScene.IsVisible = makeVisible;

            // Add to the world
            targetWorld.Scenes.Add(clonedScene);
            if (makeVisible)
                world.LoadScene(clonedScene);

            // Undo
            var capturedWorld = world;
            var capturedTargetWorld = targetWorld;
            Undo.RecordStructuralChange($"MCP Clone Scene '{newSceneName}'",
                undoAction: () => { capturedWorld.UnloadScene(clonedScene); capturedTargetWorld.Scenes.Remove(clonedScene); },
                redoAction: () => { capturedTargetWorld.Scenes.Add(clonedScene); if (makeVisible) capturedWorld.LoadScene(clonedScene); });

            return Task.FromResult(new McpToolResponse(
                $"Cloned scene '{sourceScene.Name ?? sourceScene.ID.ToString()}' as '{newSceneName}'.",
                new
                {
                    sourceSceneId = sourceScene.ID,
                    sourceSceneName = sourceScene.Name,
                    clonedSceneId = clonedScene.ID,
                    clonedSceneName = clonedScene.Name,
                    isVisible = clonedScene.IsVisible,
                    rootNodeCount = clonedScene.RootNodes.Count
                }));
        }

        // ───────────────────────────────────────────────────────────────────
        // Scene Authoring Helpers
        // ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Applies optional position, rotation, and scale overrides to a scene node.
        /// Only non-null values are applied.
        /// </summary>
        private static void ApplyTransformOverrides(
            SceneNode node,
            float? posX, float? posY, float? posZ,
            float? pitch, float? yaw, float? roll,
            float? scaleX, float? scaleY, float? scaleZ)
        {
            if (node.Transform is not Transform transform)
                return;

            bool hasPosition = posX.HasValue || posY.HasValue || posZ.HasValue;
            bool hasRotation = pitch.HasValue || yaw.HasValue || roll.HasValue;
            bool hasScale = scaleX.HasValue || scaleY.HasValue || scaleZ.HasValue;

            if (!hasPosition && !hasRotation && !hasScale)
                return;

            using var _ = Undo.TrackChange("MCP Transform Override", transform);

            if (hasPosition)
            {
                transform.Translation = new Vector3(
                    posX ?? transform.Translation.X,
                    posY ?? transform.Translation.Y,
                    posZ ?? transform.Translation.Z);
            }

            if (hasRotation)
            {
                var current = Rotator.FromQuaternion(transform.Rotation);
                transform.Rotation = new Rotator(
                    pitch ?? current.Pitch,
                    yaw ?? current.Yaw,
                    roll ?? current.Roll).ToQuaternion();
            }

            if (hasScale)
            {
                transform.Scale = new Vector3(
                    scaleX ?? transform.Scale.X,
                    scaleY ?? transform.Scale.Y,
                    scaleZ ?? transform.Scale.Z);
            }
        }

        /// <summary>
        /// Tries to set a member (property or field) on any <see cref="SceneNode"/> or <see cref="XRBase"/>-derived object.
        /// </summary>
        private static bool TrySetNodeMember(XRBase target, string memberName, object value, out string? message)
        {
            message = null;
            var targetType = target.GetType();
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;

            var property = targetType.GetProperty(memberName, flags);
            if (property is not null && property.CanWrite)
            {
                if (!McpToolRegistry.TryConvertValue(value, property.PropertyType, out var converted, out var convError))
                {
                    message = convError ?? $"Unable to convert value for '{property.Name}'.";
                    return false;
                }

                using var _ = Undo.TrackChange($"MCP Set {property.Name}", target);
                property.SetValue(target, converted);
                message = $"Set '{property.Name}' on '{targetType.Name}'.";
                return true;
            }

            message = $"Writable property '{memberName}' not found on '{targetType.Name}'.";
            return false;
        }

        /// <summary>
        /// Extracts a nullable float from a <see cref="JsonElement"/> property.
        /// </summary>
        private static float? TryGetFloat(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var prop) &&
                prop.ValueKind == JsonValueKind.Number &&
                prop.TryGetSingle(out float val))
                return val;
            return null;
        }
    }
}
