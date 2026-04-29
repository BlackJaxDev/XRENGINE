# Prefab Asset Externalization — Two-Pass Leaves-First Refactor

Status: **Implemented (core refactor landed; follow-up tests pending)**
Owner: Asset Pipeline
Related: [fbx-import-export-todo.md](fbx-import-export-todo.md), [glTF import testing](../testing/gltf-import.md)

> **Update:** The two-pass Discover / PreAssign / Write refactor is implemented in [`AssetManager.ThirdPartyImport.cs`](../../../XRENGINE/Core/Engine/AssetManager.ThirdPartyImport.cs). `KindOrder` is now leaves-first, `AnimationClip` is an externalizable kind (written to `Animations/`), and [`AnimationClipYamlTypeConverter`](../../../XRENGINE/Core/Engine/AnimationClipSerialization.cs) now emits compact `{ID}` references when `SourceAsset == self && File.Exists(FilePath)`, mirroring `XRAssetYamlConverter`. On-disk layout is documented in [`docs/features/model-import.md`](../../features/model-import.md#on-disk-externalization-layout). Placeholder files are created during Phase B and rolled back on exception. Remaining: regression tests (FBX re-import, shared-asset dedup, failure-rollback) and an audit sweep of other `XRAsset`-derived types (font glyph sets, shaders) for the same reference-emission pattern.

---

## 1. Problem Statement

After importing `Mitsuki.fbx` through the editor (native FBX backend), the resulting on-disk layout has three structural defects:

1. **AnimationClip is embedded in the root `.asset` file.** The imported `Mitsuki.asset` (the `XRPrefabSource`) contains the full `SerializedModel` of every AnimationClip inline rather than externalizing each clip to its own file. Animation data is byte-heavy; inlining it bloats the prefab file and blocks per-clip asset operations (rename, move, reuse, diff).

2. **Sub-asset content is duplicated across directories.** Each externalized sub-asset (Material, XRMesh, XRTexture, SubMesh) is written both:
   - Inside the parent container's `.asset` YAML (as an inline full object), AND
   - As its own `.asset` file in the corresponding subfolder (`Materials/`, `Meshes/`, `Textures/`, `SubMeshes/`).

   The two copies are independent; mutating one does not update the other, and on reload the in-memory instances diverge from disk.

3. **No graceful reference-graph ordering.** The current externalization pass walks asset kinds in container-first order (Model → XRMesh → SubMesh → Material → Texture). At the time a Model is written, its nested Materials/Meshes/Textures have not yet been given a `FilePath` + existing file on disk, so `XRAssetYamlConverter.ShouldWriteReference` returns `false` and the converter falls back to inline-serialization. The leaf files written later do not retroactively fix the earlier embed.

> User observation: *"after importing Mitsuki.fbx, the main Mitsuki.asset file has an embedded AnimationClip... the content of the files in the subdirs is also duplicated inside the other files... the serialization needs to be done in a particular order, where references are serialized first before nodes that reference them."*

## 2. Root Cause Analysis

All three symptoms stem from the same pipeline shape in [`XRENGINE/Core/Engine/AssetManager.ThirdPartyImport.cs`](../../../XRENGINE/Core/Engine/AssetManager.ThirdPartyImport.cs):

### 2.1 Reference gate requires an existing file
[`XRAssetYamlConverter.ShouldWriteReference`](../../../XRENGINE/Core/Engine/XRAssetYamlTypeConverter.cs) emits a compact `{ID: <guid>}` reference only when **all four** of these hold:
- `depth >= 1` (not the root)
- `SourceAsset == self` (asset self-roots the file)
- `!string.IsNullOrWhiteSpace(FilePath)`
- `File.Exists(FilePath)`

When any condition fails, the full object graph is written inline.

### 2.2 `KindOrder` walks containers before leaves
`ExternalizeEmbeddedAssetsForPrefabImport` orders kinds **container-first**:
```
Model = 0, XRMesh = 1, SubMesh = 2, XRMaterialBase = 3, XRTexture = 4
```
The Model is serialized first, but its nested Materials/Meshes/Textures have no FilePath yet → they embed inline. The leaf files written afterward are correct but redundant — the inline copies never get rewritten.

### 2.3 AnimationClip excluded from externalization
`IsRelevant` currently recognizes only the five kinds above. `AnimationClip` (`: MotionBase : XRAsset`) is never assigned a FilePath or externalized, so every reference to it fails the gate and gets inlined.

### 2.4 No pre-assignment phase
There is no phase that (a) discovers every relevant sub-asset, (b) pre-computes its final `FilePath`, (c) sets `SourceAsset = self`, and (d) creates a placeholder file so `File.Exists` returns `true` before anything serializes. The writer is forced to discover, assign, and serialize in a single pass.

## 3. Target Design — Three-Phase Externalization

Replace the single-pass externalizer with three explicit phases.

### Phase A — Discovery
Walk the imported graph (including the prefab `RootNode`'s SceneNode tree, component fields, reachable MotionBase/AnimationClip references) and collect **every** `XRAsset` instance in a flat set. Reuse `CollectReachableAssets`; extend it to traverse AnimationClip collections and component references (animators, retargeters).

### Phase B — Pre-assign & Touch
For every asset where `IsRelevant(asset) == true`:

1. Compute target directory: `<rootAssetDir>/<RootName>/<KindFolderName(kind)>/`.
2. Compute unique target filename (deduplicate on collision by appending `_<ID8>`).
3. Assign:
   - `asset.SourceAsset = asset`
   - `asset.FilePath = <absolute path>`
   - `asset.Name = <resolved name>` (if unset)
4. Ensure parent directory exists; write a **zero-byte placeholder file** to satisfy `File.Exists`. The placeholder is overwritten by Phase C.

After Phase B every reachable relevant asset has a stable identity on disk. `ShouldWriteReference` will now return `true` for every nested reference encountered during Phase C.

### Phase C — Topological Write (leaves-first)
Reverse the current `KindOrder` so leaves flush first. Proposed order:

```
0 = XRTexture
1 = XRMaterialBase
2 = SubMesh
3 = XRMesh
4 = Model
5 = AnimationClip
6 = (root prefab source — written last by the caller)
```

Within each kind, iterate assets with pre-assigned FilePath and serialize each to its file. Every nested reference in the emitted YAML is now a compact `{ID: <guid>}` pointer because the target file already exists (from Phase B's placeholder, possibly overwritten by earlier Phase C writes).

Finally, serialize the root `XRPrefabSource` to `rootAssetPath`. Its SceneNode tree embeds (intentional — the prefab is the scene-graph container), but every XRMesh/Material/Texture/AnimationClip reference encountered during tree traversal emits as a reference, not inline.

## 4. Concrete Code Changes

### 4.1 [`AssetManager.ThirdPartyImport.cs`](../../../XRENGINE/Core/Engine/AssetManager.ThirdPartyImport.cs)

- **L455 `IsRelevant`**: extend to `AnimationClip` (and any other first-class `XRAsset` leaves that are currently inlined — audit pass).
- **L436 `KindFolderName`**: add `"Animations"` for `AnimationClip`; consider renaming helpers to `GetTargetFolderName`.
- **L456 `KindOrder`**: reverse to leaves-first ordering above.
- **Split `ExternalizeEmbeddedAssetsForPrefabImport`** into three methods:
  - `DiscoverReachableAssets(rootAsset)` → `HashSet<XRAsset>`
  - `PreAssignExternalizationPaths(rootAsset, rootAssetPath, discoveredAssets)` — sets `SourceAsset`, `FilePath`, creates placeholders.
  - `WriteExternalizedAssets(discoveredAssets)` — topological write by `KindOrder`.
- **Placeholder creation**: use `new FileStream(path, FileMode.CreateNew, FileAccess.Write).Dispose()` or `File.WriteAllBytes(path, Array.Empty<byte>())` inside a try/finally with cleanup if Phase C aborts.
- **Error handling**: on Phase C exception, delete placeholders created in Phase B that were never overwritten to avoid leaving zero-byte files in the user's project.

### 4.2 [`XRAssetYamlTypeConverter.cs`](../../../XRENGINE/Core/Engine/XRAssetYamlTypeConverter.cs) (`ShouldWriteReference`, L521)

Keep the current four-condition gate. The refactor satisfies all four conditions before Phase C runs, so no converter change is strictly needed. **Optional follow-up**: relax `File.Exists` to `!string.IsNullOrWhiteSpace(FilePath)` behind an import-scoped flag if placeholders prove fragile on network drives — defer until measured.

### 4.3 AnimationClip converter

`AnimationClipYamlTypeConverter` currently inlines the `SerializedModel` even when the asset has a FilePath. Audit this converter: when the AnimationClip being serialized is at `depth >= 1` and meets `ShouldWriteReference`, it must emit the compact reference form like other XRAsset converters. Likely a single early-return branch that delegates to the base `XRAssetYamlConverter` reference emission.

### 4.4 Tests

- Reuse `XREngine.UnitTests/Rendering/NativeFbxImporterTests.cs` harness.
- Add a regression test: import `Mitsuki.fbx` (or a small synthetic FBX fixture), then assert:
  - `Mitsuki.asset` YAML contains **no** `AnimationClip:` inline block — only `{ID: ...}` references.
  - `Mitsuki.asset` YAML contains **no** full `XRMaterial`/`XRTexture2D`/`XRMesh` inline blocks — only references.
  - Every reference ID resolves to exactly one on-disk `.asset` file.
  - Round-trip load of `Mitsuki.asset` produces an equivalent prefab (mesh count, material count, animation count, texture count).
- Negative test: Phase B failure (simulated `IOException` on placeholder create) leaves the project in the pre-import state (no stray placeholder files).

## 5. Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Placeholder files left behind on mid-import failure | Try/finally cleanup; track created placeholders in a scoped list; delete unoverwritten entries on abort. |
| Shared assets across multiple root prefabs (e.g. same texture imported by two FBX) | Phase B must no-op assign if the asset already has a valid `FilePath` + `SourceAsset == self` + `File.Exists`. |
| Nested `XRPrefabSource` inside another prefab | Treat as a leaf `XRAsset` in `KindOrder` (between AnimationClip and root). Recursive handling deferred to a follow-up. |
| Name collisions across assets of the same kind | Dedup by appending the first 8 chars of the asset ID on collision. |
| Assets with no ID yet | Assign `Guid.NewGuid()` during Phase B if `ID == Guid.Empty`. |
| Users with existing project assets from the old single-pass path | Pre-v1, no back-compat owed. Re-import regenerates the correct layout. Call out in release notes. |

## 6. Rollout

- Gate the new path behind a preference during initial landing (e.g. `EditorPreferences.TwoPassAssetExternalization`, default `true`) so we can A/B against the legacy path for one milestone. Remove the flag before v1 ships.
- Update [docs/features/model-import.md](../../features/model-import.md) with the new on-disk layout description.

## 7. Acceptance Criteria

- ✅ `Mitsuki.asset` contains no inline AnimationClip / XRMaterial / XRTexture2D / XRMesh blocks after re-import.
- ✅ Each sub-asset appears **exactly once** on disk.
- ✅ Every on-disk reference resolves on load; no null / missing-asset warnings during prefab instantiation.
- ✅ Build is clean; no new warnings introduced.
- ✅ Full FBX + glTF unit test suites green.
- ✅ Visual smoke test: Mitsuki prefab instantiates in Editor world with materials, meshes, and bound animations.

## 8. Implementation Checklist

- [ ] Extend `IsRelevant` / `KindFolderName` / `KindOrder` for `AnimationClip`.
- [ ] Reverse `KindOrder` to leaves-first.
- [ ] Split externalizer into Discover / PreAssign / Write phases.
- [ ] Add placeholder-file creation + abort cleanup.
- [ ] Audit `AnimationClipYamlTypeConverter` for reference-emission path; fix if inlining unconditionally.
- [ ] Sweep other `XRAsset`-derived types for the same pattern (font glyph sets, shaders, etc.).
- [ ] Add FBX re-import regression test.
- [ ] Add shared-asset dedup test.
- [ ] Add failure-rollback test.
- [ ] Update `docs/features/model-import.md` on-disk layout section.
- [ ] Release note entry.

## 9. Out of Scope (Follow-ups)

- Cross-prefab asset deduplication on repeat imports (hash-based reuse of existing `.asset` files for identical textures/meshes).
- Asset GC when a root prefab is deleted.
- Incremental re-import (diff sub-assets against existing files).

---

*See also: [/memories/repo/](../../../) notes on YAML serialization and FBX pipeline for internal context.*
