# Undo System Coverage Audit

**Date:** 2026-02-27  
**Last Updated:** 2026-02-28  
**Scope:** `XREngine.Editor/` — all ImGui panels, component editors, transform editors, drag-drop handlers, MCP actions.

> **All gaps are now fixed.** Previous P0–P3 ImGui items were fixed first. MCP action gaps (1–10), RigidBody transform gap, and PropertyEditor gaps (A–G) were fixed in the final pass.

---

## Fixed in P3 Pass (2026-02-28)

- **GPULandscapeComponentEditor — TerrainLayer Properties**: `TerrainLayer` converted to derive from `XRBase` with `SetField` backing. All 8 layer property edits (Name, Tint, UV Tiling, UV Offset, Metallic, Roughness, Normal Strength, Height Strength) now tracked via `ImGuiUndoHelper.TrackDragUndo`.
- **CameraComponentEditor — Post-Processing State**: Threaded `CameraComponent` through the schema editor call chain. All 13 `state.SetValue` sites now undo-tracked — drag widgets via `ImGuiUndoHelper.TrackDragUndo`, instant widgets (Checkbox, Combo/Selectable, luminance preset buttons) via `Undo.TrackChange`. Undo target is `stageState.BackingInstance as XRBase` when available, falling back to the component.
- **ModelComponentEditor — Shader Uniform Overrides**: All 7 `ShaderVar.SetValue` calls now undo-tracked. `ShaderVar` already derives from `XRBase`. DragFloat/DragInt/DragFloat2-4 use `ImGuiUndoHelper.TrackDragUndo`; Checkbox uses `Undo.TrackChange`.
- **HumanoidComponentEditor — IK, General, and Target sections**: SolveIK and debug visibility checkboxes via `Undo.TrackChange`. 5 IK toggle checkboxes via `Undo.TrackChange` (threaded `humanoid` parameter). 11 target rows: drag-drop node assignment, "Use Selected", and "Clear" buttons via `Undo.TrackChange`; offset DragFloat3 via `ImGuiUndoHelper.TrackDragUndo`.
- **Hierarchy Panel — Drag-Drop Reparent**: Implemented full `BeginDragDropTarget` receiver in the ImGui hierarchy panel. Accepts `ImGuiSceneNodeDragDrop` payloads with cycle-prevention (`IsHierarchyDescendantOf`). Structural undo via `Undo.RecordStructuralChange` correctly manages root-node list membership.
- **PropertyEditor — GetOrCreatePersistentCalls / Add+Remove Callback**: "Add Callback" and "X" (Remove) buttons now use `Undo.RecordStructuralChange` with proper undo/redo actions that insert/remove `XRPersistentCall` entries and re-notify the owner.

---

## Fixed in P2 Pass (2026-02-28)

- **HierarchyPanel** — Scene visibility toggle (`ToggleSceneVisibility`)
- **DirectionalLightComponentEditor** — Shadow volume scale, cascade count, cascade overlap (3 sites)
- **ModelComponentEditor** — Model asset, IsActive after impostor, ApplySharedMaterial, LOD material overrides, renderer material (7 sites)
- **RigidBodyComponentEditors** — Checkboxes (gravity, simulation, debug, sleep), InputInt/InputText (collision group, dominance, owner, actor name), body flags, lock flags, DragFloat properties (damping, velocity, mass, inertia, CenterOfMass, CCD, depenetration, contact, stabilization, sleep, wake, solver iterations, linear/angular velocity) — both dynamic AND static editors, including creation settings (auto-create, density, shape offset/rotation) (~50+ sites total)
- **GPULandscapeComponentEditor** — Module enabled checkbox (conditional XRBase cast)
- **SteamAudioGeometryComponentEditor** — Material preset picker, absorption/scattering/transmission sliders (4 sites)
- **UIMaterialComponentEditor** — Material asset picker (1 site)
- **PropertyEditor** — Asset picker callbacks: DrawAssetFieldForProperty, DrawSimplePropertyRow asset field, DrawSimpleFieldRow asset field, Clear button, array replacement, TryCreateAndSetPropertyValue, DrawNullComplexField Create, XREvent Create, XREvent Set Null (9 sites)
- **MCP MoveNodeSiblingAsync** — Structural undo for parent sibling reorder + root node reorder (2 paths)

---

## MCP Action Undo Coverage Audit (2026-02-27)

Audited all 8 files in `XREngine.Editor/Mcp/Actions/`:

| File | Action Count | With Undo | Gaps | Read-Only/Non-Mutating |
|------|-------------|-----------|------|------------------------|
| EditorMcpActions.Scene.cs | 21 | 12 | 0 | 9 |
| EditorMcpActions.Components.cs | 5 | 3 | 0 | 2 |
| EditorMcpActions.Transform.cs | 2 | 2 | 0 | 0 |
| EditorMcpActions.World.cs | 10 | 5 | 0 | 5 |
| EditorMcpActions.Workflow.cs | 11 | 2 | 0 | 9 |
| EditorMcpActions.Viewport.cs | 1 | 0 | 0 | 1 |
| EditorMcpActions.Introspection.cs | 16 | 0 | 0 | 16 |
| EditorMcpActions.Helpers.cs | 0 | 0 | 0 | 0 |
| **Total** | **66** | **24** | **0** | **42** |

### Covered (14 actions with proper undo)

#### EditorMcpActions.Scene.cs
- **ReparentNodeAsync** (L141–166) — `Undo.RecordStructuralChange` ✅
- **DeleteSceneNodeAsync** (L178–229) — `Undo.RecordStructuralChange` (soft-delete + restore) ✅
- **CreateSceneNodeAsync** (L248–320) — `Undo.TrackSceneNode` + `Undo.RecordStructuralChange` ✅
- **RenameSceneNodeAsync** (L328–340) — `Undo.TrackChange("MCP Rename Node", node)` ✅
- **DuplicateSceneNodeAsync** (L349–415) — `Undo.TrackSceneNode` + `Undo.RecordStructuralChange` ✅
- **MoveNodeSiblingAsync** (L424–544) — `Undo.RecordStructuralChange` (both parent/root paths) ✅
- **SetNodeWorldTransformAsync** (L842–888) — `Undo.TrackChange("MCP Set World Transform", transform)` ✅
- **SetNodeTransformAsync** (L827–838) — delegates to `SetTransformAsync` ✅

#### EditorMcpActions.Components.cs
- **AddComponentToNodeAsync** (L36–71) — `Undo.TrackChange` + `Undo.RecordStructuralChange` ✅
- **RemoveComponentAsync** (L197–223) — `Undo.RecordStructuralChange` ✅

#### EditorMcpActions.Transform.cs
- **SetTransformAsync** (L39–95) — `Undo.TrackChange("MCP Set Transform", transform)` ✅
- **RotateTransformAsync** (L110–137) — `Undo.TrackChange("MCP Rotate Transform", transform)` ✅

#### EditorMcpActions.Workflow.cs
- **DeleteSelectedNodesAsync** (L63–109) — `Undo.RecordStructuralChange` ✅
- **CreatePrimitiveShapeAsync** (L188–274) — `Undo.TrackSceneNode` + `Undo.RecordStructuralChange` ✅

---

### GAPS — Property Mutations Without Undo (10 actions) — ALL FIXED

#### GAP 1: `SetNodeActiveAsync` — EditorMcpActions.Scene.cs ✅ FIXED
Added `Undo.TrackChange("MCP Set Node Active", node)` before assignment.

#### GAP 2: `SetNodeActiveRecursiveAsync` — EditorMcpActions.Scene.cs ✅ FIXED
Added `Undo.TrackChange("MCP Set Node Active Recursive", entry)` per node in loop.

#### GAP 3: `SetLayerAsync` — EditorMcpActions.Scene.cs ✅ FIXED
Added `Undo.TrackChange("MCP Set Layer", node)` before assignment.

#### GAP 4: `InstantiatePrefabAsync` — EditorMcpActions.Scene.cs ✅ FIXED
Added `Undo.TrackSceneNode(instance)` + `Undo.RecordStructuralChange` for both root-level and parented paths.

#### GAP 5: `SetComponentPropertyAsync` — EditorMcpActions.Components.cs ✅ FIXED
Added `Undo.TrackChange` before both `property.SetValue` and `field.SetValue` paths.

#### GAP 6: `CreateSceneAsync` — EditorMcpActions.World.cs ✅ FIXED
Added `Undo.RecordStructuralChange("MCP Create Scene", ...)` with undo (UnloadScene+Remove) / redo (Add+LoadScene).

#### GAP 7: `DeleteSceneAsync` — EditorMcpActions.World.cs ✅ FIXED
Captured scene index and visibility. Added `Undo.RecordStructuralChange` with undo (Insert+LoadScene) / redo (Unload+Remove).

#### GAP 8: `ToggleSceneVisibilityAsync` — EditorMcpActions.World.cs ✅ FIXED
Added `Undo.TrackChange("MCP Toggle Scene Visibility", scene)` before property + load/unload.

#### GAP 9: `SetActiveSceneAsync` — EditorMcpActions.World.cs ✅ FIXED
Captured old index and visibility. Added `Undo.RecordStructuralChange` restoring original position and visibility.

#### GAP 10: `ImportSceneAsync` — EditorMcpActions.World.cs ✅ FIXED
Captured wasInList and originalVisibility. Added `Undo.RecordStructuralChange` with proper undo/redo.

---

### Borderline / Low Priority

| Action | File | Reason |
|--------|------|--------|
| `SetTagAsync` | Scene.cs L761 | Tags stored in `ConditionalWeakTable` (ephemeral runtime-only), not persisted. Undo optional. |
| `LoadWorldAsync` | Workflow.cs L320 | Replaces `TargetWorld` entirely. Typically treated as a non-undoable navigation operation (like "Open File"). |
| `CreatePrefabFromNodeAsync` | Scene.cs L904 | Creates an asset file on disk. Asset creation is typically not undoable at the scene level. |

---

### Read-Only / Non-Mutating Actions (42 total — no undo needed)

These actions only query state or perform selection/camera operations:

- ListSceneNodesAsync, GetSceneNodeInfoAsync, FindNodesByNameAsync, FindNodesByTypeAsync
- SelectNodeAsync, FocusNodeInViewAsync, ListLayersAsync, ListTagsAsync
- GetNodeWorldTransformAsync, GetTransformDecomposedAsync, GetTransformMatricesAsync
- ListComponentsAsync, GetComponentPropertyAsync
- ListWorldsAsync, ListScenesAsync, ExportSceneAsync, ValidateSceneAsync
- CaptureViewportScreenshotAsync
- UndoAsync, RedoAsync, ClearSelectionAsync, SelectNodeByNameAsync
- EnterPlayModeAsync, ExitPlayModeAsync, SaveWorldAsync, ListToolsAsync
- All 16 Introspection actions (ListComponentTypesAsync, GetComponentSchemaAsync, etc.)
- ListTransformChildrenAsync

---

## TransformEditors/ Audit (2026-02-27)

**Scope:** All 6 `.cs` files in `XREngine.Editor/TransformEditors/`.

### Summary

| File | Status | Gaps |
|------|--------|------|
| `IXRTransformEditor.cs` | **N/A** — interface only, no widgets | 0 |
| `TransformEditorUtil.cs` | **N/A** — read-only helpers only (`TextDisabled`) | 0 |
| `StandardTransformEditor.cs` | **Full coverage** | 0 |
| `UITransformEditor.cs` | **Full coverage** | 0 |
| `UIBoundableTransformEditor.cs` | **Full coverage** | 0 |
| `RigidBodyTransformEditor.cs` | **Full coverage** (gap fixed) | 0 |

### Detailed File Analysis

#### IXRTransformEditor.cs — No widgets
Interface definition only. Nothing to track.

#### TransformEditorUtil.cs — No mutations
`GetTransformDisplayName` returns a label string. `DrawReadOnlyVector3` calls `ImGui.TextDisabled` (read-only). No mutations.

#### StandardTransformEditor.cs — Full coverage (4/4 widgets)
- **L36** `DragFloat3` Translation → `TrackDragUndo` L37 ✅
- **L51** `DragFloat3` Rotation → `TrackDragUndo` L52 ✅
- **L68** `DragFloat3` Scale → `TrackDragUndo` L69 ✅
- **L86** `Combo` Order → `TrackDragUndo` L87 ✅

#### UITransformEditor.cs — Full coverage (7/7 widgets)
- **L38** `InputText` Styling ID → `TrackDragUndo` L39/L46 (both branches) ✅
- **L50** `InputText` Styling Class → `TrackDragUndo` L52/L59 (both branches) ✅
- **L72** `DragFloat2` Translation → `TrackDragUndo` L73 ✅
- **L89** `DragFloat` Depth → `TrackDragUndo` L90 ✅
- **L106** `DragFloat3` Scale → `TrackDragUndo` L107 ✅
- **L123** `DragFloat` Rotation → `TrackDragUndo` L124 ✅

#### UIBoundableTransformEditor.cs — Full coverage (12/12 widgets)
Delegates to `UITransformEditor` for base fields (covered above), then:
- **L42–L47** 6× `DrawOptionalFloat` (Width, Height, MinWidth, MinHeight, MaxWidth, MaxHeight) — each internally has `Checkbox` + `DragFloat`, both tracked via `TrackDragUndo` (L166, L174, L188) ✅
- **L54** `DragFloat2` Pivot → `TrackDragUndo` L55 ✅
- **L77** `DragFloat2` Min Anchor → `TrackDragUndo` L78 ✅
- **L92** `DragFloat2` Max Anchor → `TrackDragUndo` L93 ✅
- **L115** `DragFloat4` Margins → `TrackDragUndo` L116 ✅
- **L129** `DragFloat4` Padding → `TrackDragUndo` L130 ✅

#### RigidBodyTransformEditor.cs — 0 gaps (all fixed)

All 7 widgets covered:
- **L49** `Combo` Interpolation Mode → `TrackDragUndo` L50 ✅
- **L66** `DragFloat3` Position Offset → `TrackDragUndo` L67 ✅
- **L84** `DragFloat3` Pre/Post Rotation Offset → `TrackDragUndo` L86 ✅
- **L129** `DragFloat3` Body Position → `TrackDragUndo` ✅ (was missing, now fixed)
- **L137** `DragFloat3` Body Rotation → `TrackDragUndo` L144 ✅

---

## EditorImGuiUI.*.cs Full File Audit (2026-02-27)

**Scope:** All 21 files matching `XREngine.Editor/IMGUI/EditorImGuiUI.*.cs`.
**Patterns audited:**
1. `Undo.TrackChange("desc", target)` — instant widget mutations (Checkbox, Button, Selectable)
2. `ImGuiUndoHelper.TrackDragUndo("desc", target)` — drag/slider widgets, called AFTER the widget
3. `Undo.RecordStructuralChange("desc", undoAction, redoAction)` — structural changes

### Summary

| # | File | Status | Gap Count |
|---|------|--------|-----------|
| 1 | EditorImGuiUI.ArchiveImport.cs | **EXEMPT** — transient import dialog, no scene mutations | 0 |
| 2 | EditorImGuiUI.ArchiveInspectorPanel.cs | **EXEMPT** — read-only preview/diagnostics panel | 0 |
| 3 | EditorImGuiUI.AssetExplorerPanel.cs | **EXEMPT** — internal UI state (search, filters) + file ops | 0 |
| 4 | EditorImGuiUI.ConsolePanel.cs | **EXEMPT** — internal UI state (auto-scroll, filter) | 0 |
| 5 | EditorImGuiUI.HierarchyPanel.cs | **Full coverage** | 0 |
| 6 | EditorImGuiUI.Icons.cs | **EXEMPT** — no widgets (icon loading only) | 0 |
| 7 | EditorImGuiUI.ImGui.cs | **EXEMPT** — close-prompt dialog, no scene mutations | 0 |
| 8 | EditorImGuiUI.InspectorPanel.cs | **Full coverage** | 0 |
| 9 | EditorImGuiUI.Mipmap2DInspector.cs | **EXEMPT** — reimport file I/O only | 0 |
| 10 | EditorImGuiUI.MissingAssetsPanel.cs | **EXEMPT** — diagnostics/file repair panel | 0 |
| 11 | EditorImGuiUI.ModelDropSpawn.cs | **Full coverage** | 0 |
| 12 | EditorImGuiUI.NetworkingPanel.cs | **EXEMPT** — session networking config, not scene data | 0 |
| 13 | EditorImGuiUI.OpenGLPanel.cs | **EXEMPT** — read-only diagnostics/inspection panel | 0 |
| 14 | EditorImGuiUI.ProfilerPanel.cs | **EXEMPT** — editor preferences + internal UI panel toggles | 0 |
| 15 | EditorImGuiUI.PropertyEditor.cs | **Full coverage** (all gaps fixed) | 0 |
| 16 | EditorImGuiUI.RenderPipelineGraphPanel.cs | **EXEMPT** — visualization-only panel, no mutation widgets | 0 |
| 17 | EditorImGuiUI.SettingsPanel.cs | **EXEMPT** — "Save" buttons only (file I/O) | 0 |
| 18 | EditorImGuiUI.ShaderGraphPanel.cs | **EXEMPT** — transient editor tool state (in-memory graph) | 0 |
| 19 | EditorImGuiUI.StatePanel.cs | **EXEMPT** — play mode session operations | 0 |
| 20 | EditorImGuiUI.Toolbar.cs | **EXEMPT** — editor tool state (transform mode/space/snap) + play mode | 0 |
| 21 | EditorImGuiUI.ViewportPanel.cs | **Full coverage** | 0 |

### Fully Covered Files — Details

#### EditorImGuiUI.HierarchyPanel.cs
- L259 `Checkbox("##ActiveSelf")` → `Undo.TrackChange("Toggle Node Active", node)` at L264 ✅
- L238 `InputText("##Rename")` → `Undo.TrackChange("Rename Node", node)` at L458 ✅
- L522 `Checkbox("Visible##SceneVisible")` → `Undo.TrackChange("Toggle Scene Visibility", scene)` at L655 ✅
- L526 `SmallButton("Unload")` → `Undo.RecordStructuralChange("Unload Scene"...)` at L677 ✅
- L387 context menu "Delete" → `Undo.RecordStructuralChange($"Delete {nodeName}"...)` ✅
- L431 context menu "Create Child" → `Undo.RecordStructuralChange("Create Child Node"...)` ✅
- L779 drag-drop reparent → `Undo.RecordStructuralChange($"Reparent {nodeName}"...)` ✅
- L95 `Checkbox("Show Editor Scene")` → internal UI state (`_showEditorSceneHierarchy`). EXEMPT.
- L834 `SmallButton("Settings")` → opens inspector. EXEMPT.

#### EditorImGuiUI.InspectorPanel.cs
- L678 `InputText("##SceneNodeName")` → `ImGuiUndoHelper.TrackDragUndo("Rename Node", node)` at L683 ✅
- L692 `Checkbox("##SceneNodeActiveSelf")` → `Undo.TrackChange("Toggle Active Self", node)` at L694 ✅
- L702 `Checkbox("##SceneNodeActiveInHierarchy")` → `Undo.TrackChange("Toggle Active In Hierarchy", node)` at L704 ✅
- L410 `InputTextWithHint("##SceneNodeNameMulti")` → `ImGuiUndoHelper.TrackDragUndo("Rename Node", node)` at L416 ✅
- L430 `Checkbox("##SceneNodeActiveSelfMulti")` → `Undo.Track(node)` at L442 ✅
- L453 `Checkbox("##SceneNodeActiveInHierarchyMulti")` → `Undo.Track(node)` at L462 ✅
- L986 `Checkbox("##ComponentActive")` → `Undo.TrackChange("Toggle Component Active", component)` at L988 ✅
- L1235 `Checkbox("##ComponentActiveMulti")` → `Undo.Track(component)` at L1245 ✅
- L998 `SmallButton("Rename")` → triggers popup, apply via `Undo.TrackChange("Rename Component"...)` at L52 ✅
- L1007 `SmallButton("Remove")` (single) → `Undo.RecordStructuralChange($"Remove {compName}"...)` at L1103 ✅
- L1252 `SmallButton("Remove")` (multi) → `Undo.RecordStructuralChange($"Remove {compName}"...)` at L1280 ✅
- L175 `Selectable` (add component) → `Undo.RecordStructuralChange($"Add {compName}"...)` at L232 ✅
- L824 `Selectable` (change transform type) → `Undo.Track(node)` + `Undo.Track(current)` in `TryChangeTransformType` at L922 ✅

#### EditorImGuiUI.ModelDropSpawn.cs
- L200 `Undo.RecordStructuralChange("Spawn Prefab"...)` ✅
- L260 `Undo.RecordStructuralChange("Spawn Model"...)` ✅

#### EditorImGuiUI.ViewportPanel.cs
- L274 `Undo.TrackChange("Drop Material", renderer)` ✅

---

### GAP DETAILS — EditorImGuiUI.PropertyEditor.cs — ALL FIXED

All 7 gap areas have been fixed.

#### GAP A: Collection Simple Element Values (`DrawCollectionSimpleElement`) ✅ FIXED
Added `owner` parameter and `XRBase? undoTarget` resolution. Checkbox/Enum/Set/Clear → `Undo.TrackChange`. InputText/DragFloat2/3/4 → `ImGuiUndoHelper.TrackDragUndo`.

#### GAP B: Dictionary Simple Element Values (`DrawDictionarySimpleElement`) ✅ FIXED
Added `undoTarget` resolution. Checkbox/Enum → `Undo.TrackChange`. InputText/DragFloat2/3/4 → `ImGuiUndoHelper.TrackDragUndo`. Set/Clear buttons restructured to call undo before mutation.

#### GAP C: Collection Structural Changes (Add/Remove/Replace/Create elements) ✅ FIXED
Added `Undo.RecordStructuralChange` to:
- Collection Remove button (captures item before removal, undo re-inserts)
- `TryAddCollectionInstance` (undo removes at inserted index)
- `TryReplaceCollectionInstance` (captures previous value, undo restores)
- Plain "Add Element" button (undo removes at end)
- `TryAddDictionaryEntry` (undo removes the key)
- `TryRemoveDictionaryEntry` (captures value, undo re-adds the key+value)
- `TryReplaceDictionaryInstance` (captures previous, undo restores)

#### GAP D: `DrawInlineValueEditor` / Override Settings ✅ FIXED
Added `ImGuiUndoHelper.TrackDragUndo` after `DrawInlineValueEditor` calls in `DrawOverrideableSettingRow` (both base and override paths). Also added `UpdateInspectorUndoScope` after the fallback `DrawInlineValueEditor` call in `DrawSimpleFieldRow`.

#### GAP E: Override Toggle Checkbox ✅ FIXED
Added `Undo.TrackChange("Toggle Override", undoTarget)` before `setting.HasOverride = checkboxValue`.

#### GAP F: Persistent Call Node/Method Selection ✅ FIXED
- `DrawPersistentCallNodePickerButton`: Added `Undo.TrackChange("Set Callback Node", undoTarget)` in the node picker callback.
- `DrawPersistentCallMethodCombo`: Added `Undo.TrackChange("Clear Callback Method", undoTarget)` for Clear button and `Undo.TrackChange("Set Callback Method", undoTarget)` for method Selectable.

#### GAP G: SceneNode picker in `DrawPersistentCallNodePickerButton` ✅ FIXED
Same fix as GAP F — the inner node picker callback now has `Undo.TrackChange`.

---

### Priority Assessment — ALL COMPLETE

All gaps have been fixed. No remaining undo coverage issues.
