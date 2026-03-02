using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using XREngine;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.Editor.Mcp
{
    public sealed partial class EditorMcpActions
    {
        private sealed record WorldSnapshotRecord(
            string Id,
            string Name,
            string SerializedWorld,
            Guid SourceWorldId,
            DateTime CreatedUtc);

        private static readonly ConcurrentDictionary<string, WorldSnapshotRecord> WorldSnapshots = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, string> ActiveTransactions = new(StringComparer.OrdinalIgnoreCase);

        [XRMcp(Name = "validate_scene_integrity", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Perform deep integrity validation on a scene hierarchy (null roots, parent mismatches, cycles, duplicate IDs, world binding issues).")]
        public static Task<McpToolResponse> ValidateSceneIntegrityAsync(
            McpToolContext context,
            [McpName("scene_name"), Description("Optional scene name or GUID; defaults to first active scene.")]
            string? sceneName = null)
        {
            var world = context.WorldInstance;
            if (!TryResolveScene(world, sceneName, out var scene, out var sceneError) || scene is null)
                return Task.FromResult(new McpToolResponse(sceneError ?? "Scene not found.", isError: true));

            var errors = new List<string>();
            var warnings = new List<string>();

            if (scene.RootNodes.Any(node => node is null))
                errors.Add("Scene contains null root node entries.");

            var visited = new HashSet<Guid>();
            var recursion = new HashSet<Guid>();

            foreach (var root in scene.RootNodes)
            {
                if (root is null)
                    continue;

                if (root.Parent is not null)
                    errors.Add($"Root node '{root.Name ?? root.ID.ToString()}' has a non-null parent.");

                ValidateNodeRecursive(root, world, visited, recursion, errors, warnings, isRoot: true);
            }

            int duplicateRootIds = scene.RootNodes
                .Where(n => n is not null)
                .GroupBy(n => n!.ID)
                .Count(g => g.Count() > 1);
            if (duplicateRootIds > 0)
                errors.Add($"Scene has {duplicateRootIds} duplicate root node ID group(s).");

            var result = new
            {
                sceneId = scene.ID,
                sceneName = scene.Name,
                errorCount = errors.Count,
                warningCount = warnings.Count,
                errors,
                warnings
            };

            return Task.FromResult(new McpToolResponse(
                errors.Count == 0
                    ? (warnings.Count == 0 ? "Scene integrity validation passed." : $"Scene integrity validation passed with {warnings.Count} warning(s).")
                    : $"Scene integrity validation failed with {errors.Count} error(s).",
                result,
                isError: errors.Count > 0));
        }

        [XRMcp(Name = "bulk_reparent_nodes", Permission = McpPermissionLevel.Mutate, PermissionReason = "Reparents multiple scene nodes in one operation.")]
        [Description("Reparent multiple scene nodes to a new parent (or root) in one call.")]
        public static Task<McpToolResponse> BulkReparentNodesAsync(
            McpToolContext context,
            [McpName("node_ids"), Description("Array of scene node GUIDs to reparent.")]
            string[] nodeIds,
            [McpName("new_parent_id"), Description("Optional target parent node GUID. Omit/null to move to scene root.")]
            string? newParentId = null,
            [McpName("scene_name"), Description("Target scene when reparenting to root. Optional; defaults to first active scene.")]
            string? sceneName = null,
            [McpName("preserve_world_transform"), Description("Preserve world transforms while reparenting.")]
            bool preserveWorldTransform = true)
        {
            if (nodeIds is null || nodeIds.Length == 0)
                return Task.FromResult(new McpToolResponse("node_ids must contain at least one node ID.", isError: true));

            var world = context.WorldInstance;
            var targetScene = ResolveScene(world, sceneName);
            if (targetScene is null)
                return Task.FromResult(new McpToolResponse("No active scene found.", isError: true));

            var uniqueIds = nodeIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var targets = new List<SceneNode>();
            foreach (string id in uniqueIds)
            {
                if (!TryGetNodeById(world, id, out var node, out var error) || node is null)
                    return Task.FromResult(new McpToolResponse(error ?? $"Node '{id}' not found.", isError: true));
                targets.Add(node);
            }

            SceneNode? newParent = null;
            if (!string.IsNullOrWhiteSpace(newParentId))
            {
                if (!TryGetNodeById(world, newParentId!, out newParent, out var parentError) || newParent is null)
                    return Task.FromResult(new McpToolResponse(parentError ?? "New parent node not found.", isError: true));
            }

            foreach (var node in targets)
            {
                if (newParent is not null)
                {
                    if (ReferenceEquals(node, newParent))
                        return Task.FromResult(new McpToolResponse($"Node '{node.Name}' cannot be parented to itself.", isError: true));

                    if (IsDescendantOf(newParent, node))
                        return Task.FromResult(new McpToolResponse($"Cannot parent '{node.Name}' under its descendant '{newParent.Name}'.", isError: true));
                }
            }

            var changes = targets.Select(node => new ReparentChange(
                Node: node,
                OldParent: node.Parent,
                OldScene: FindSceneForNode(node, world),
                NewParent: newParent,
                NewScene: targetScene)).ToArray();

            using var interaction = Undo.BeginUserInteraction();
            using var scope = Undo.BeginChange("MCP Bulk Reparent Nodes");

            foreach (var change in changes)
                ApplyReparentChange(change, world, preserveWorldTransform);

            Undo.RecordStructuralChange("Bulk Reparent Nodes",
                undoAction: () =>
                {
                    foreach (var change in changes)
                        RevertReparentChange(change, world, preserveWorldTransform);
                },
                redoAction: () =>
                {
                    foreach (var change in changes)
                        ApplyReparentChange(change, world, preserveWorldTransform);
                });

            return Task.FromResult(new McpToolResponse(
                $"Reparented {changes.Length} node(s).",
                new
                {
                    count = changes.Length,
                    newParentId = newParent?.ID,
                    nodes = changes.Select(c => new { id = c.Node.ID, name = c.Node.Name, path = BuildNodePath(c.Node) }).ToArray()
                }));
        }

        [XRMcp(Name = "prefab_apply_overrides", Permission = McpPermissionLevel.Destructive, PermissionReason = "Writes prefab asset changes to disk.")]
        [Description("Apply an instance's recorded prefab overrides back to its source prefab asset.")]
        public static Task<McpToolResponse> PrefabApplyOverridesAsync(
            McpToolContext context,
            [McpName("node_id"), Description("Any node within a prefab instance hierarchy.")]
            string nodeId,
            [McpName("save_prefab"), Description("Save prefab asset to disk after applying overrides.")]
            bool savePrefab = true,
            [McpName("clear_instance_overrides"), Description("Clear overrides from the instance after successful apply.")]
            bool clearInstanceOverrides = false)
        {
            if (!TryGetNodeById(context.WorldInstance, nodeId, out var node, out var error) || node is null)
                return Task.FromResult(new McpToolResponse(error ?? "Scene node not found.", isError: true));

            SceneNode instanceRoot = FindPrefabInstanceRoot(node);
            Guid prefabAssetId = instanceRoot.Prefab?.PrefabAssetId ?? Guid.Empty;
            if (prefabAssetId == Guid.Empty)
                return Task.FromResult(new McpToolResponse("Node is not linked to a prefab instance.", isError: true));

            var assets = Engine.Assets;
            if (assets is null)
                return Task.FromResult(new McpToolResponse("Asset manager is unavailable.", isError: true));

            var prefab = assets.GetAssetByID(prefabAssetId) as XRPrefabSource;
            if (prefab is null)
                return Task.FromResult(new McpToolResponse($"Prefab asset '{prefabAssetId}' not found.", isError: true));
            if (prefab.RootNode is null)
                return Task.FromResult(new McpToolResponse("Prefab has no root node.", isError: true));

            List<SceneNodePrefabNodeOverride> overrides = SceneNodePrefabUtility.ExtractOverrides(instanceRoot);
            int propertyOverrideCount = overrides.Sum(x => x.Properties.Count);
            if (propertyOverrideCount == 0)
                return Task.FromResult(new McpToolResponse("No recorded overrides found on the prefab instance.", new { prefabId = prefab.ID, applied = 0 }));

            SceneNodePrefabUtility.ApplyOverrides(prefab.RootNode, overrides);

            if (savePrefab)
                assets.Save(prefab, bypassJobThread: true);

            if (clearInstanceOverrides)
                ClearPrefabOverridesRecursive(instanceRoot);

            return Task.FromResult(new McpToolResponse(
                $"Applied {propertyOverrideCount} override(s) back to prefab '{prefab.Name ?? prefab.ID.ToString()}'.",
                new
                {
                    prefabId = prefab.ID,
                    prefabName = prefab.Name,
                    nodeOverrideCount = overrides.Count,
                    propertyOverrideCount,
                    saved = savePrefab,
                    clearedInstanceOverrides = clearInstanceOverrides
                }));
        }

        [XRMcp(Name = "prefab_revert_overrides", Permission = McpPermissionLevel.Mutate, PermissionReason = "Mutates prefab instance values by reverting recorded overrides.")]
        [Description("Revert recorded prefab overrides on an instance by restoring source prefab values.")]
        public static Task<McpToolResponse> PrefabRevertOverridesAsync(
            McpToolContext context,
            [McpName("node_id"), Description("Any node within a prefab instance hierarchy.")]
            string nodeId)
        {
            if (!TryGetNodeById(context.WorldInstance, nodeId, out var node, out var error) || node is null)
                return Task.FromResult(new McpToolResponse(error ?? "Scene node not found.", isError: true));

            SceneNode instanceRoot = FindPrefabInstanceRoot(node);
            Guid prefabAssetId = instanceRoot.Prefab?.PrefabAssetId ?? Guid.Empty;
            if (prefabAssetId == Guid.Empty)
                return Task.FromResult(new McpToolResponse("Node is not linked to a prefab instance.", isError: true));

            var assets = Engine.Assets;
            if (assets is null)
                return Task.FromResult(new McpToolResponse("Asset manager is unavailable.", isError: true));

            var prefab = assets.GetAssetByID(prefabAssetId) as XRPrefabSource;
            if (prefab is null || prefab.RootNode is null)
                return Task.FromResult(new McpToolResponse("Source prefab asset/root not found.", isError: true));

            var templateClone = SceneNodePrefabUtility.CloneHierarchy(prefab.RootNode);
            var templateMap = SceneNodePrefabUtility.BuildPrefabNodeMap(templateClone);
            var instanceMap = SceneNodePrefabUtility.BuildPrefabNodeMap(instanceRoot);

            List<SceneNodePrefabNodeOverride> overrides = SceneNodePrefabUtility.ExtractOverrides(instanceRoot);
            int reverted = 0;
            int failed = 0;

            using var interaction = Undo.BeginUserInteraction();
            using var scope = Undo.BeginChange("MCP Prefab Revert Overrides");

            foreach (var nodeOverride in overrides)
            {
                if (!instanceMap.TryGetValue(nodeOverride.PrefabNodeId, out var targetNode))
                    continue;
                if (!templateMap.TryGetValue(nodeOverride.PrefabNodeId, out var templateNode))
                    continue;

                foreach (var key in nodeOverride.Properties.Keys.ToArray())
                {
                    if (TryReadPropertyPathValue(templateNode, key, out var templateValue) &&
                        TryWritePropertyPathValue(targetNode, key, templateValue, out _))
                    {
                        _ = SceneNodePrefabUtility.RemovePropertyOverride(targetNode, key);
                        reverted++;
                    }
                    else
                    {
                        failed++;
                    }
                }
            }

            return Task.FromResult(new McpToolResponse(
                $"Reverted {reverted} override value(s)" + (failed > 0 ? $", {failed} failed." : "."),
                new
                {
                    prefabId = prefabAssetId,
                    reverted,
                    failed
                },
                isError: reverted == 0 && failed > 0));
        }

        [XRMcp(Name = "snapshot_world_state", Permission = McpPermissionLevel.Mutate, PermissionReason = "Captures world state into MCP in-memory snapshot storage.")]
        [Description("Capture an in-memory snapshot of the active world state for later restore.")]
        public static Task<McpToolResponse> SnapshotWorldStateAsync(
            McpToolContext context,
            [McpName("name"), Description("Optional snapshot name.")]
            string? name = null)
        {
            var world = context.WorldInstance.TargetWorld;
            if (world is null)
                return Task.FromResult(new McpToolResponse("No active world found.", isError: true));

            if (!TrySerializeYamlForMcp(world, "snapshot_world_state", out string serialized, out var serializationError))
                return Task.FromResult(serializationError!);

            string snapshotId = Guid.NewGuid().ToString("N")[..12];
            string snapshotName = string.IsNullOrWhiteSpace(name)
                ? $"Snapshot-{DateTime.UtcNow:yyyyMMdd-HHmmss}"
                : name!;

            WorldSnapshots[snapshotId] = new WorldSnapshotRecord(
                snapshotId,
                snapshotName,
                serialized,
                world.ID,
                DateTime.UtcNow);

            return Task.FromResult(new McpToolResponse(
                $"Captured world snapshot '{snapshotName}'.",
                new
                {
                    snapshotId,
                    snapshotName,
                    worldId = world.ID,
                    worldName = world.Name,
                    createdUtc = DateTime.UtcNow.ToString("o")
                }));
        }

        [XRMcp(Name = "restore_world_state", Permission = McpPermissionLevel.Destructive, PermissionReason = "Replaces active world with a snapshot.")]
        [Description("Restore the active world from a previously captured snapshot.")]
        public static Task<McpToolResponse> RestoreWorldStateAsync(
            McpToolContext context,
            [McpName("snapshot_id"), Description("Snapshot ID returned by snapshot_world_state.")]
            string snapshotId)
        {
            if (string.IsNullOrWhiteSpace(snapshotId))
                return Task.FromResult(new McpToolResponse("snapshot_id is required.", isError: true));

            if (!WorldSnapshots.TryGetValue(snapshotId, out var snapshot))
                return Task.FromResult(new McpToolResponse($"Snapshot '{snapshotId}' was not found.", isError: true));

            if (!TryDeserializeYamlForMcp(snapshot.SerializedWorld, "restore_world_state", out XRWorld? restored, out var deserializationError))
                return Task.FromResult(deserializationError!);

            var worldInstance = context.WorldInstance;
            var previous = worldInstance.TargetWorld;

            using var interaction = Undo.BeginUserInteraction();
            using var scope = Undo.BeginChange("MCP Restore World State");

            worldInstance.TargetWorld = restored;
            Selection.Clear();

            Undo.RecordStructuralChange("Restore World Snapshot",
                undoAction: () =>
                {
                    worldInstance.TargetWorld = previous;
                    Selection.Clear();
                },
                redoAction: () =>
                {
                    worldInstance.TargetWorld = restored;
                    Selection.Clear();
                });

            return Task.FromResult(new McpToolResponse(
                $"Restored snapshot '{snapshot.Name}'.",
                new
                {
                    snapshotId = snapshot.Id,
                    snapshotName = snapshot.Name,
                    restoredWorldId = restored.ID,
                    restoredWorldName = restored.Name
                }));
        }

        [XRMcp(Name = "run_editor_command", Permission = McpPermissionLevel.Mutate, PermissionReason = "Executes allowlisted editor workflow commands.")]
        [Description("Execute an allowlisted editor command via MCP (undo/redo/selection/play-mode/save/load/focus/select).")]
        public static Task<McpToolResponse> RunEditorCommandAsync(
            McpToolContext context,
            [McpName("command"), Description("Allowlisted command name.")]
            string command,
            [McpName("arguments"), Description("Optional JSON object of command arguments.")]
            JsonElement? arguments = null)
        {
            if (string.IsNullOrWhiteSpace(command))
                return Task.FromResult(new McpToolResponse("command is required.", isError: true));

            string cmd = command.Trim().ToLowerInvariant();
            JsonElement args = arguments ?? default;

            return cmd switch
            {
                "undo" => UndoAsync(context),
                "redo" => RedoAsync(context),
                "clear_selection" => ClearSelectionAsync(context),
                "delete_selected_nodes" => DeleteSelectedNodesAsync(context),
                "enter_play_mode" => EnterPlayModeAsync(context),
                "exit_play_mode" => ExitPlayModeAsync(context),
                "save_world" => SaveWorldAsync(context, TryGetStringArg(args, "output_dir")),
                "load_world" => LoadWorldAsync(context, TryGetStringArg(args, "asset_path"), TryGetStringArg(args, "world_name")),
                "focus_node" => FocusNodeInViewAsync(context,
                    TryGetStringArg(args, "node_id") ?? string.Empty,
                    TryGetFloatArg(args, "duration") ?? 0.35f),
                "select_node" => SelectNodeAsync(context,
                    TryGetStringArg(args, "node_id"),
                    TryGetStringArrayArg(args, "node_ids"),
                    TryGetBoolArg(args, "append") ?? false),
                _ => Task.FromResult(new McpToolResponse(
                    $"Unsupported command '{command}'. Supported: undo, redo, clear_selection, delete_selected_nodes, enter_play_mode, exit_play_mode, save_world, load_world, focus_node, select_node.",
                    isError: true))
            };
        }

        [XRMcp(Name = "list_asset_import_options", Permission = McpPermissionLevel.ReadOnly)]
        [Description("List third-party import options for a source asset path.")]
        public static Task<McpToolResponse> ListAssetImportOptionsAsync(
            McpToolContext context,
            [McpName("source_path"), Description("Source file path relative to game assets.")]
            string sourcePath,
            [McpName("asset_type"), Description("Optional asset type (e.g., XRPrefabSource, XRTexture2D).")]
            string? assetType = null)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
                return Task.FromResult(new McpToolResponse("source_path is required.", isError: true));

            if (!TryGetGameAssetsPath(out string assetsPath, out var assetsError))
                return Task.FromResult(assetsError!);

            string fullSourcePath = ResolveAndValidateGamePath(assetsPath, sourcePath, out var pathError, mustExist: true, expectDirectory: false);
            if (pathError is not null)
                return Task.FromResult(pathError);

            var assets = Engine.Assets;
            if (assets is null)
                return Task.FromResult(new McpToolResponse("Asset manager is unavailable.", isError: true));

            if (!TryResolveImportAssetType(fullSourcePath, assetType, out var resolvedType, out var typeError) || resolvedType is null)
                return Task.FromResult(new McpToolResponse(typeError ?? "Unable to resolve asset type.", isError: true));

            if (!assets.TryGetThirdPartyImportContext(fullSourcePath, resolvedType, out var importOptions, out var optionsPath, out var generatedAssetPath) || importOptions is null)
                return Task.FromResult(new McpToolResponse("Failed to resolve import options context for source path.", isError: true));

            var properties = ReadSettingsProperties(importOptions, category: null);

            return Task.FromResult(new McpToolResponse(
                $"Resolved import options for '{sourcePath}'.",
                new
                {
                    sourcePath = fullSourcePath,
                    assetType = resolvedType.FullName,
                    optionsPath,
                    generatedAssetPath,
                    propertyCount = properties.Count,
                    properties
                }));
        }

        [XRMcp(Name = "set_asset_import_options", Permission = McpPermissionLevel.Destructive, PermissionReason = "Writes import option files and optionally triggers reimport.")]
        [Description("Set a third-party import option property and save it.")]
        public static Task<McpToolResponse> SetAssetImportOptionsAsync(
            McpToolContext context,
            [McpName("source_path"), Description("Source file path relative to game assets.")]
            string sourcePath,
            [McpName("property_name"), Description("Import option property name.")]
            string propertyName,
            [McpName("value"), Description("JSON value to assign.")]
            object value,
            [McpName("asset_type"), Description("Optional asset type (e.g., XRPrefabSource, XRTexture2D).")]
            string? assetType = null,
            [McpName("reimport"), Description("Reimport source after saving options.")]
            bool reimport = true)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
                return Task.FromResult(new McpToolResponse("source_path is required.", isError: true));
            if (string.IsNullOrWhiteSpace(propertyName))
                return Task.FromResult(new McpToolResponse("property_name is required.", isError: true));

            if (!TryGetGameAssetsPath(out string assetsPath, out var assetsError))
                return Task.FromResult(assetsError!);

            string fullSourcePath = ResolveAndValidateGamePath(assetsPath, sourcePath, out var pathError, mustExist: true, expectDirectory: false);
            if (pathError is not null)
                return Task.FromResult(pathError);

            var assets = Engine.Assets;
            if (assets is null)
                return Task.FromResult(new McpToolResponse("Asset manager is unavailable.", isError: true));

            if (!TryResolveImportAssetType(fullSourcePath, assetType, out var resolvedType, out var typeError) || resolvedType is null)
                return Task.FromResult(new McpToolResponse(typeError ?? "Unable to resolve asset type.", isError: true));

            if (!assets.TryGetThirdPartyImportContext(fullSourcePath, resolvedType, out var importOptions, out _, out _) || importOptions is null)
                return Task.FromResult(new McpToolResponse("Failed to resolve import options context for source path.", isError: true));

            if (!TrySetReflectedProperty(importOptions, propertyName, value, out var setError))
                return Task.FromResult(new McpToolResponse(setError ?? "Failed to set import option property.", isError: true));

            bool saved = assets.SaveThirdPartyImportOptions(fullSourcePath, resolvedType, importOptions);
            if (!saved)
                return Task.FromResult(new McpToolResponse("Failed to save import options.", isError: true));

            bool reimported = false;
            if (reimport)
                reimported = assets.ReimportThirdPartyFile(fullSourcePath);

            return Task.FromResult(new McpToolResponse(
                $"Updated import option '{propertyName}' for '{sourcePath}'.",
                new
                {
                    sourcePath = fullSourcePath,
                    assetType = resolvedType.FullName,
                    propertyName,
                    saved,
                    reimportRequested = reimport,
                    reimported
                }));
        }

        [XRMcp(Name = "diff_scene_nodes", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Diff two scene node hierarchies and return structural/property differences.")]
        public static Task<McpToolResponse> DiffSceneNodesAsync(
            McpToolContext context,
            [McpName("left_node_id"), Description("Left node GUID.")]
            string leftNodeId,
            [McpName("right_node_id"), Description("Right node GUID.")]
            string rightNodeId,
            [McpName("include_children"), Description("Include recursive hierarchy comparison.")]
            bool includeChildren = true)
        {
            var world = context.WorldInstance;
            if (!TryGetNodeById(world, leftNodeId, out var left, out var leftErr) || left is null)
                return Task.FromResult(new McpToolResponse(leftErr ?? "Left node not found.", isError: true));
            if (!TryGetNodeById(world, rightNodeId, out var right, out var rightErr) || right is null)
                return Task.FromResult(new McpToolResponse(rightErr ?? "Right node not found.", isError: true));

            var differences = new List<object>();
            CompareNodeAtPath(left, right, "$", differences);

            if (includeChildren)
            {
                var leftMap = BuildHierarchyPathMap(left);
                var rightMap = BuildHierarchyPathMap(right);

                var allPaths = leftMap.Keys.Union(rightMap.Keys, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

                foreach (string path in allPaths)
                {
                    bool hasLeft = leftMap.TryGetValue(path, out var ln);
                    bool hasRight = rightMap.TryGetValue(path, out var rn);

                    if (!hasLeft)
                    {
                        differences.Add(new { path, kind = "missing_left", rightNode = rn?.Name });
                        continue;
                    }
                    if (!hasRight)
                    {
                        differences.Add(new { path, kind = "missing_right", leftNode = ln?.Name });
                        continue;
                    }

                    if (path == "$")
                        continue;

                    CompareNodeAtPath(ln!, rn!, path, differences);
                }
            }

            return Task.FromResult(new McpToolResponse(
                differences.Count == 0
                    ? "No differences found between scene nodes."
                    : $"Found {differences.Count} difference(s).",
                new
                {
                    leftId = left.ID,
                    rightId = right.ID,
                    includeChildren,
                    differenceCount = differences.Count,
                    differences
                }));
        }

        [XRMcp(Name = "query_references", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Query references for assets, scene nodes, and components by GUID.")]
        public static Task<McpToolResponse> QueryReferencesAsync(
            McpToolContext context,
            [McpName("target_id"), Description("GUID of asset/node/component.")]
            string targetId,
            [McpName("target_type"), Description("Optional: auto, asset, node, component.")]
            string targetType = "auto")
        {
            if (string.IsNullOrWhiteSpace(targetId))
                return Task.FromResult(new McpToolResponse("target_id is required.", isError: true));
            if (!Guid.TryParse(targetId, out var guid))
                return Task.FromResult(new McpToolResponse("target_id must be a valid GUID.", isError: true));

            if (!XRObjectBase.ObjectsCache.TryGetValue(guid, out var obj) || obj is null)
                return Task.FromResult(new McpToolResponse($"Object '{targetId}' not found.", isError: true));

            string normalized = targetType.Trim().ToLowerInvariant();

            if (normalized is "auto" or "asset")
            {
                if (obj is XRAsset asset)
                    return BuildAssetReferenceResponse(context, asset);
                if (normalized == "asset")
                    return Task.FromResult(new McpToolResponse("target_id is not an asset.", isError: true));
            }

            if (normalized is "auto" or "node")
            {
                if (obj is SceneNode node)
                    return BuildNodeReferenceResponse(node);
                if (normalized == "node")
                    return Task.FromResult(new McpToolResponse("target_id is not a scene node.", isError: true));
            }

            if (normalized is "auto" or "component")
            {
                if (obj is XRComponent component)
                    return BuildComponentReferenceResponse(component);
                if (normalized == "component")
                    return Task.FromResult(new McpToolResponse("target_id is not a component.", isError: true));
            }

            return Task.FromResult(new McpToolResponse($"Unsupported target type '{targetType}'.", isError: true));
        }

        [XRMcp(Name = "transaction_begin", Permission = McpPermissionLevel.Mutate, PermissionReason = "Starts a logical MCP transaction using snapshot storage.")]
        [Description("Begin an MCP transaction by capturing a rollback snapshot of the current world state.")]
        public static Task<McpToolResponse> TransactionBeginAsync(
            McpToolContext context,
            [McpName("name"), Description("Optional transaction name.")]
            string? name = null)
        {
            var world = context.WorldInstance.TargetWorld;
            if (world is null)
                return Task.FromResult(new McpToolResponse("No active world found.", isError: true));

            string snapshotId = Guid.NewGuid().ToString("N")[..12];
            string transactionId = Guid.NewGuid().ToString("N")[..12];
            string txName = string.IsNullOrWhiteSpace(name) ? $"Transaction-{DateTime.UtcNow:HHmmss}" : name!;

            if (!TrySerializeYamlForMcp(world, "transaction_begin", out string serialized, out var serializationError))
                return Task.FromResult(serializationError!);

            WorldSnapshots[snapshotId] = new WorldSnapshotRecord(
                snapshotId,
                $"{txName}-snapshot",
                serialized,
                world.ID,
                DateTime.UtcNow);

            ActiveTransactions[transactionId] = snapshotId;

            return Task.FromResult(new McpToolResponse(
                $"Transaction '{txName}' started.",
                new
                {
                    transactionId,
                    name = txName,
                    snapshotId,
                    worldId = world.ID
                }));
        }

        [XRMcp(Name = "transaction_commit", Permission = McpPermissionLevel.Mutate, PermissionReason = "Commits and discards transaction rollback snapshot.")]
        [Description("Commit an MCP transaction and discard its rollback snapshot.")]
        public static Task<McpToolResponse> TransactionCommitAsync(
            McpToolContext context,
            [McpName("transaction_id"), Description("Transaction ID from transaction_begin.")]
            string transactionId)
        {
            if (string.IsNullOrWhiteSpace(transactionId))
                return Task.FromResult(new McpToolResponse("transaction_id is required.", isError: true));

            if (!ActiveTransactions.TryRemove(transactionId, out var snapshotId))
                return Task.FromResult(new McpToolResponse($"Transaction '{transactionId}' not found.", isError: true));

            _ = WorldSnapshots.TryRemove(snapshotId, out _);

            return Task.FromResult(new McpToolResponse(
                $"Committed transaction '{transactionId}'.",
                new { transactionId, snapshotId, committed = true }));
        }

        [XRMcp(Name = "transaction_rollback", Permission = McpPermissionLevel.Destructive, PermissionReason = "Restores world state from transaction snapshot.")]
        [Description("Rollback an MCP transaction by restoring the snapshot captured at begin.")]
        public static Task<McpToolResponse> TransactionRollbackAsync(
            McpToolContext context,
            [McpName("transaction_id"), Description("Transaction ID from transaction_begin.")]
            string transactionId,
            [McpName("keep_open"), Description("Keep transaction active after rollback.")]
            bool keepOpen = false)
        {
            if (string.IsNullOrWhiteSpace(transactionId))
                return Task.FromResult(new McpToolResponse("transaction_id is required.", isError: true));

            if (!ActiveTransactions.TryGetValue(transactionId, out var snapshotId))
                return Task.FromResult(new McpToolResponse($"Transaction '{transactionId}' not found.", isError: true));

            if (!WorldSnapshots.TryGetValue(snapshotId, out var snapshot))
                return Task.FromResult(new McpToolResponse($"Snapshot '{snapshotId}' for transaction was not found.", isError: true));

            if (!TryDeserializeYamlForMcp(snapshot.SerializedWorld, "transaction_rollback", out XRWorld? restored, out var deserializationError))
                return Task.FromResult(deserializationError!);

            var worldInstance = context.WorldInstance;
            var previous = worldInstance.TargetWorld;

            using var interaction = Undo.BeginUserInteraction();
            using var scope = Undo.BeginChange("MCP Transaction Rollback");

            worldInstance.TargetWorld = restored;
            Selection.Clear();

            Undo.RecordStructuralChange("Transaction Rollback",
                undoAction: () =>
                {
                    worldInstance.TargetWorld = previous;
                    Selection.Clear();
                },
                redoAction: () =>
                {
                    worldInstance.TargetWorld = restored;
                    Selection.Clear();
                });

            if (!keepOpen)
            {
                _ = ActiveTransactions.TryRemove(transactionId, out _);
                _ = WorldSnapshots.TryRemove(snapshotId, out _);
            }

            return Task.FromResult(new McpToolResponse(
                $"Rolled back transaction '{transactionId}'.",
                new
                {
                    transactionId,
                    snapshotId,
                    keepOpen,
                    restoredWorldId = restored.ID,
                    restoredWorldName = restored.Name
                }));
        }

        private sealed record ReparentChange(
            SceneNode Node,
            SceneNode? OldParent,
            XRScene? OldScene,
            SceneNode? NewParent,
            XRScene? NewScene);

        private static void ApplyReparentChange(ReparentChange change, XRWorldInstance world, bool preserveWorldTransform)
        {
            if (change.NewParent is not null)
            {
                RemoveNodeFromSceneRoots(change.Node, change.OldScene, world);
                change.Node.Transform.SetParent(change.NewParent.Transform, preserveWorldTransform, EParentAssignmentMode.Immediate);
            }
            else
            {
                change.Node.Transform.SetParent(null, preserveWorldTransform, EParentAssignmentMode.Immediate);
                if (change.NewScene is not null)
                {
                    if (!change.NewScene.RootNodes.Contains(change.Node))
                        change.NewScene.RootNodes.Add(change.Node);
                    if (change.NewScene.IsVisible && !world.RootNodes.Contains(change.Node))
                        world.RootNodes.Add(change.Node);
                }
            }
        }

        private static void RevertReparentChange(ReparentChange change, XRWorldInstance world, bool preserveWorldTransform)
        {
            if (change.OldParent is not null)
            {
                RemoveNodeFromSceneRoots(change.Node, change.NewScene, world);
                change.Node.Transform.SetParent(change.OldParent.Transform, preserveWorldTransform, EParentAssignmentMode.Immediate);
            }
            else
            {
                change.Node.Transform.SetParent(null, preserveWorldTransform, EParentAssignmentMode.Immediate);
                if (change.OldScene is not null)
                {
                    if (!change.OldScene.RootNodes.Contains(change.Node))
                        change.OldScene.RootNodes.Add(change.Node);
                    if (change.OldScene.IsVisible && !world.RootNodes.Contains(change.Node))
                        world.RootNodes.Add(change.Node);
                }
            }
        }

        private static void RemoveNodeFromSceneRoots(SceneNode node, XRScene? scene, XRWorldInstance world)
        {
            if (scene is null)
                scene = FindSceneForNode(node, world);

            scene?.RootNodes.Remove(node);
            world.RootNodes.Remove(node);
        }

        private static bool IsDescendantOf(SceneNode potentialDescendant, SceneNode ancestor)
        {
            SceneNode? current = potentialDescendant.Parent;
            while (current is not null)
            {
                if (ReferenceEquals(current, ancestor))
                    return true;
                current = current.Parent;
            }
            return false;
        }

        private static void ValidateNodeRecursive(
            SceneNode node,
            XRWorldInstance world,
            HashSet<Guid> visited,
            HashSet<Guid> recursion,
            List<string> errors,
            List<string> warnings,
            bool isRoot)
        {
            if (!visited.Add(node.ID))
                warnings.Add($"Duplicate traversal encounter for node '{node.Name ?? node.ID.ToString()}'.");

            if (!recursion.Add(node.ID))
            {
                errors.Add($"Cycle detected at node '{node.Name ?? node.ID.ToString()}'.");
                return;
            }

            if (!isRoot && node.Parent is null)
                errors.Add($"Non-root node '{node.Name ?? node.ID.ToString()}' has null parent.");

            if (node.World != world)
                warnings.Add($"Node '{node.Name ?? node.ID.ToString()}' world binding does not match active world instance.");

            foreach (var child in GetChildren(node))
            {
                if (!ReferenceEquals(child.Parent, node))
                    errors.Add($"Child '{child.Name ?? child.ID.ToString()}' parent mismatch under '{node.Name ?? node.ID.ToString()}'.");

                ValidateNodeRecursive(child, world, visited, recursion, errors, warnings, isRoot: false);
            }

            recursion.Remove(node.ID);
        }

        private static SceneNode FindPrefabInstanceRoot(SceneNode node)
        {
            SceneNode current = node;
            SceneNode? probe = node;
            while (probe is not null)
            {
                if (probe.Prefab?.IsPrefabRoot == true)
                    return probe;
                current = probe;
                probe = probe.Parent;
            }

            return current;
        }

        private static void ClearPrefabOverridesRecursive(SceneNode root)
        {
            foreach (var node in EnumerateHierarchy(root))
                node.Prefab?.PropertyOverrides.Clear();
        }

        private static bool TryReadPropertyPathValue(object root, string propertyPath, out object? value)
        {
            value = null;
            if (root is null || string.IsNullOrWhiteSpace(propertyPath))
                return false;

            object? current = root;
            string[] segments = propertyPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
            foreach (string segment in segments)
            {
                if (current is null)
                    return false;

                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
                var prop = current.GetType().GetProperty(segment, flags);
                if (prop is not null && prop.CanRead)
                {
                    current = prop.GetValue(current);
                    continue;
                }

                var field = current.GetType().GetField(segment, flags);
                if (field is not null)
                {
                    current = field.GetValue(current);
                    continue;
                }

                return false;
            }

            value = current;
            return true;
        }

        private static bool TryWritePropertyPathValue(object root, string propertyPath, object? value, out string? error)
        {
            error = null;
            if (root is null || string.IsNullOrWhiteSpace(propertyPath))
            {
                error = "Invalid property path target.";
                return false;
            }

            object? current = root;
            string[] segments = propertyPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                error = "Property path is empty.";
                return false;
            }

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                string segment = segments[i];
                var prop = current?.GetType().GetProperty(segment, flags);
                if (prop is not null && prop.CanRead)
                {
                    current = prop.GetValue(current);
                    continue;
                }

                var field = current?.GetType().GetField(segment, flags);
                if (field is not null)
                {
                    current = field.GetValue(current);
                    continue;
                }

                error = $"Path segment '{segment}' could not be resolved.";
                return false;
            }

            if (current is null)
            {
                error = "Final owner object is null.";
                return false;
            }

            string finalMember = segments[^1];
            var ownerType = current.GetType();
            var finalProp = ownerType.GetProperty(finalMember, flags);
            if (finalProp is not null && finalProp.CanWrite)
            {
                if (!McpToolRegistry.TryConvertValue(value, finalProp.PropertyType, out var converted, out var convErr))
                {
                    error = convErr ?? $"Cannot convert value for '{finalProp.Name}'.";
                    return false;
                }

                finalProp.SetValue(current, converted);
                return true;
            }

            error = $"Writable property '{finalMember}' was not found on '{ownerType.Name}'.";
            return false;
        }

        private static bool TryResolveImportAssetType(string fullSourcePath, string? explicitAssetType, out Type? resolvedType, out string? error)
        {
            resolvedType = null;
            error = null;

            if (!string.IsNullOrWhiteSpace(explicitAssetType))
            {
                if (!TryResolveAssetType(explicitAssetType!, out resolvedType) || resolvedType is null)
                {
                    error = $"Asset type '{explicitAssetType}' was not found.";
                    return false;
                }
                return true;
            }

            var assets = Engine.Assets;
            if (assets is not null)
            {
                var matched = assets.LoadedAssetsByIDInternal.Values
                    .FirstOrDefault(x => string.Equals(x.OriginalPath, fullSourcePath, StringComparison.OrdinalIgnoreCase));
                if (matched is not null)
                {
                    resolvedType = matched.GetType();
                    return true;
                }
            }

            resolvedType = typeof(XRPrefabSource);
            return true;
        }

        private static bool TrySetReflectedProperty(object target, string propertyName, object value, out string? error)
        {
            error = null;
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            var prop = target.GetType().GetProperty(propertyName, flags);
            if (prop is null || !prop.CanWrite)
            {
                error = $"Writable property '{propertyName}' was not found on '{target.GetType().Name}'.";
                return false;
            }

            if (!McpToolRegistry.TryConvertValue(value, prop.PropertyType, out var converted, out var convError))
            {
                error = convError ?? $"Unable to convert value for '{prop.Name}'.";
                return false;
            }

            prop.SetValue(target, converted);
            return true;
        }

        private static Dictionary<string, SceneNode> BuildHierarchyPathMap(SceneNode root)
        {
            var map = new Dictionary<string, SceneNode>(StringComparer.OrdinalIgnoreCase);
            BuildHierarchyPathMapRecursive(root, "$", map);
            return map;
        }

        private static void BuildHierarchyPathMapRecursive(SceneNode node, string path, Dictionary<string, SceneNode> map)
        {
            map[path] = node;
            var children = GetChildren(node).ToArray();
            for (int i = 0; i < children.Length; i++)
                BuildHierarchyPathMapRecursive(children[i], $"{path}/{i}", map);
        }

        private static void CompareNodeAtPath(SceneNode left, SceneNode right, string path, List<object> differences)
        {
            if (!string.Equals(left.Name, right.Name, StringComparison.Ordinal))
                differences.Add(new { path, kind = "name", left = left.Name, right = right.Name });

            if (left.IsActiveSelf != right.IsActiveSelf)
                differences.Add(new { path, kind = "active", left = left.IsActiveSelf, right = right.IsActiveSelf });

            if (left.Components.Count != right.Components.Count)
                differences.Add(new { path, kind = "component_count", left = left.Components.Count, right = right.Components.Count });

            var leftTypes = left.Components.Select(c => c.GetType().FullName ?? c.GetType().Name).OrderBy(x => x).ToArray();
            var rightTypes = right.Components.Select(c => c.GetType().FullName ?? c.GetType().Name).OrderBy(x => x).ToArray();
            if (!leftTypes.SequenceEqual(rightTypes, StringComparer.Ordinal))
                differences.Add(new { path, kind = "component_types", left = leftTypes, right = rightTypes });

            if (left.Transform is Transform ltf && right.Transform is Transform rtf)
            {
                if (ltf.Translation != rtf.Translation)
                    differences.Add(new { path, kind = "translation", left = ltf.Translation, right = rtf.Translation });
                if (ltf.Rotation != rtf.Rotation)
                    differences.Add(new { path, kind = "rotation", left = ltf.Rotation, right = rtf.Rotation });
                if (ltf.Scale != rtf.Scale)
                    differences.Add(new { path, kind = "scale", left = ltf.Scale, right = rtf.Scale });
            }
        }

        private static Task<McpToolResponse> BuildAssetReferenceResponse(McpToolContext context, XRAsset targetAsset)
        {
            var referencingAssets = new List<object>();
            var assets = Engine.Assets;
            if (assets is not null)
            {
                foreach (var loaded in assets.LoadedAssetsByIDInternal.Values)
                {
                    if (ReferenceEquals(loaded, targetAsset))
                        continue;
                    if (loaded.EmbeddedAssets.Contains(targetAsset))
                    {
                        referencingAssets.Add(new
                        {
                            assetId = loaded.ID,
                            name = loaded.Name,
                            type = loaded.GetType().FullName,
                            filePath = loaded.FilePath
                        });
                    }
                }
            }

            var nodeRefs = new List<object>();
            var world = context.WorldInstance;
            foreach (var scene in world.TargetWorld?.Scenes ?? [])
            {
                foreach (var root in scene.RootNodes)
                {
                    if (root is null)
                        continue;
                    CollectAssetReferences(root, targetAsset, nodeRefs);
                }
            }

            return Task.FromResult(new McpToolResponse(
                $"Found {referencingAssets.Count} asset reference(s) and {nodeRefs.Count} scene-node reference(s).",
                new
                {
                    targetId = targetAsset.ID,
                    targetType = "asset",
                    targetName = targetAsset.Name,
                    referencingAssets,
                    sceneNodeReferences = nodeRefs
                }));
        }

        private static Task<McpToolResponse> BuildNodeReferenceResponse(SceneNode node)
        {
            var children = GetChildren(node).ToArray();
            var tags = GetTags(node).ToArray();

            return Task.FromResult(new McpToolResponse(
                "Resolved scene node references.",
                new
                {
                    targetId = node.ID,
                    targetType = "node",
                    nodeName = node.Name,
                    parentId = node.Parent?.ID,
                    childCount = children.Length,
                    children = children.Select(c => new { id = c.ID, name = c.Name }).ToArray(),
                    componentCount = node.Components.Count,
                    components = node.Components.Select(c => new { id = c.ID, name = c.Name, type = c.GetType().FullName }).ToArray(),
                    tags
                }));
        }

        private static Task<McpToolResponse> BuildComponentReferenceResponse(XRComponent component)
        {
            var references = new List<object>();
            var flags = BindingFlags.Instance | BindingFlags.Public;
            foreach (var prop in component.GetType().GetProperties(flags))
            {
                if (!typeof(XRAsset).IsAssignableFrom(prop.PropertyType) || !prop.CanRead)
                    continue;

                try
                {
                    if (prop.GetValue(component) is XRAsset asset)
                    {
                        references.Add(new
                        {
                            property = prop.Name,
                            assetId = asset.ID,
                            assetName = asset.Name,
                            assetType = asset.GetType().FullName,
                            filePath = asset.FilePath
                        });
                    }
                }
                catch
                {
                }
            }

            return Task.FromResult(new McpToolResponse(
                "Resolved component references.",
                new
                {
                    targetId = component.ID,
                    targetType = "component",
                    componentName = component.Name,
                    componentType = component.GetType().FullName,
                    nodeId = component.SceneNode?.ID,
                    references
                }));
        }

        private static string? TryGetStringArg(JsonElement args, string key)
        {
            if (args.ValueKind != JsonValueKind.Object)
                return null;
            if (!args.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
                return null;
            return el.GetString();
        }

        private static bool? TryGetBoolArg(JsonElement args, string key)
        {
            if (args.ValueKind != JsonValueKind.Object)
                return null;
            if (!args.TryGetProperty(key, out var el) || (el.ValueKind != JsonValueKind.True && el.ValueKind != JsonValueKind.False))
                return null;
            return el.GetBoolean();
        }

        private static float? TryGetFloatArg(JsonElement args, string key)
        {
            if (args.ValueKind != JsonValueKind.Object)
                return null;
            if (!args.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number)
                return null;
            return el.TryGetSingle(out float v) ? v : null;
        }

        private static string[]? TryGetStringArrayArg(JsonElement args, string key)
        {
            if (args.ValueKind != JsonValueKind.Object)
                return null;
            if (!args.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Array)
                return null;

            return el.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Cast<string>()
                .ToArray();
        }

        private static bool TrySerializeYamlForMcp(object instance, string operation, out string yaml, out McpToolResponse? error)
        {
            yaml = string.Empty;
            error = null;

            try
            {
                yaml = AssetManager.Serializer.Serialize(instance);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MCP] YAML serialization failed during '{operation}' for '{instance.GetType().FullName}': {ex}");
                error = new McpToolResponse($"Serialization failed during '{operation}'. See the general log for details.", isError: true);
                return false;
            }
        }

        private static bool TryDeserializeYamlForMcp<T>(string yaml, string operation, [NotNullWhen(true)] out T? value, out McpToolResponse? error) where T : class
        {
            value = null;
            error = null;

            try
            {
                value = AssetManager.Deserializer.Deserialize<T>(yaml);
                if (value is null)
                {
                    Debug.LogError($"[MCP] YAML deserialization returned null during '{operation}' for '{typeof(T).FullName}'.");
                    error = new McpToolResponse($"Deserialization failed during '{operation}'. See the general log for details.", isError: true);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MCP] YAML deserialization failed during '{operation}' for '{typeof(T).FullName}': {ex}");
                error = new McpToolResponse($"Deserialization failed during '{operation}'. See the general log for details.", isError: true);
                return false;
            }
        }
    }
}
