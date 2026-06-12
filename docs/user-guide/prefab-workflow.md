# Prefab Workflow

This document summarizes the current prefab pipeline and the new utilities that wire it into runtime/editor flows.

## Creating prefab assets

1. Author or select a scene node hierarchy in the world/editor.
2. Call `SceneNodePrefabUtility.CreatePrefabAsset(rootNode, "AssetName", targetDirectory)` to clone the hierarchy, stamp prefab metadata, and write an `XRPrefabSource` asset.
3. The generated prefab asset keeps stable GUIDs per node so overrides can be tracked later.

### Serialized transform shape

- Default `SceneNode.Transform` values now serialize directly under `Transform:` without the extra `$value` wrapper.
- Derived transform types still emit `$type` and `$value` so their concrete runtime type stays explicit on disk.
- Prefab-local transform references such as `SubMesh.RootBone` and `SubMesh.RootTransform` still serialize as compact `ID` mappings.

## Recording overrides and variants

- While editing an instantiated prefab, use `SceneNodePrefabUtility.RecordPropertyOverride(node, "Transform.Position", newValue)` (or the higher-level tooling being built in the editor) to capture per-node overrides.
- When you are happy with the changes, run `SceneNodePrefabUtility.CreateVariantAsset(instanceRoot, basePrefab, "VariantName", targetDirectory)` to create an `XRPrefabVariant`. The utility automatically calls `SceneNodePrefabUtility.ExtractOverrides` so the variant only stores deltas.
- Use `SceneNodePrefabUtility.CaptureOverrides(instanceRoot)` whenever you need the raw override payload (e.g., to preview differences or sync with source control).

## Instantiating prefabs at runtime

**AssetManager helpers**

- `AssetManager.InstantiatePrefab(XRPrefabSource prefab, XRWorldInstance? world, SceneNode? parent, bool maintainWorldTransform)` clones an in-memory prefab and optionally binds it to a world/parent.
- `AssetManager.InstantiatePrefab(string assetPath, ...)` and `InstantiatePrefab(Guid prefabId, ...)` load on demand from disk or cache. Async counterparts are available for path-based loads.
- `AssetManager.InstantiateVariant(...)` mirrors the same overload set for `XRPrefabVariant` assets. These methods are attributed with `RequiresUnreferencedCode` because prefab overrides rely on reflection.

**XRWorldInstance shortcuts**

- `XRWorldInstance.InstantiatePrefab(...)` and `InstantiateVariant(...)` wrap the asset manager/service helpers and automatically add the spawned hierarchy to `RootNodes` when no parent is provided.
- Overloads exist for prefab/variant instances, asset IDs, and asset paths, so gameplay systems can choose whichever handle they already have.

## Refreshing or breaking prefab links

- To replay serialized overrides on an existing hierarchy, use `SceneNodePrefabUtility.ApplyVariantOverrides(instanceRoot, variant)`.
- To permanently detach an instance from its source asset, call `SceneNodePrefabUtility.BreakPrefabLink(instanceRoot)`, which clears all prefab metadata.

## Inspecting prefab assets in the ImGui editor

- Selecting an `XRPrefabSource` asset in the ImGui asset browser now shows a prefab hierarchy tree in the inspector.
- Clicking a node in that tree opens a sub-inspector below it, reusing the standard scene-node inspector for the selected prefab node's name, transform, and components.
- The inspector keeps its own per-prefab node selection, so browsing a prefab asset does not replace the active world hierarchy selection.
- The prefab hierarchy tree supports search by node name or path, right-click actions for rename/add-child/duplicate/delete, and drag-drop reparenting within the prefab tree.
- Root-node-only safeguards match prefab semantics: the root can be renamed and extended with children, but it is not offered delete or duplicate actions that would detach the prefab from its single-root structure.

## Where to integrate in the editor

- Menu or toolbar actions can now call into `SceneNodePrefabUtility.CreatePrefabAsset` / `CreateVariantAsset` to persist authoring changes.
- Inspector widgets should invoke `SceneNodePrefabUtility.RecordPropertyOverride` when a prefab instance field changes so overrides stay in sync.
- Scene hierarchy context actions can use the new `XRWorldInstance` helpers to spawn prefabs directly into the active world or parent selection.
