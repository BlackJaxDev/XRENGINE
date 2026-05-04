# Native Hierarchy Panel — Full Porting Plan

## Current State

The native `HierarchyPanel.cs` (547 lines) in `XREngine.Editor/UI/Panels/` is a flat virtual-scrolling list with depth indentation, single-click select, double-click focus, and drag-drop reparenting. The ImGui `EditorImGuiUI.HierarchyPanel.cs` (684 lines) in `XREngine.Editor/IMGUI/` is a full tree editor with 12 additional features.

### Already Working in Native

- Virtual-scrolling flat list with depth indentation
- Single-click selection (`Selection.SceneNode`)
- Double-click focus camera on node
- Drag & drop reparenting (with ancestor check)
- Drop preview text + highlight
- Custom scrollbar
- Truncation limit (2000 nodes)
- Background blur material

### Gap Summary

| # | Feature | Difficulty | Dependencies |
|---|---------|-----------|--------------|
| 1 | **Expand/collapse tree nodes** | High | New expand arrow component; collapsed state dict; incremental `RemakeChildren` |
| 2 | **Multi-selection** (Ctrl/Shift/Alt) | Medium | Keyboard modifier detection in native input |
| 3 | **Right-click context menu** | High | **No native popup/context-menu component exists** — must build from scratch |
| 4 | **Inline rename** | Medium | `UITextInputComponent` needs single-line mode + Escape cancel + Enter commit |
| 5 | **Active/enabled checkbox** per node | Low | `UIToggleComponent` exists; just wire it in |
| 6 | **Scene-grouped sections** with collapsible headers | Medium | Per-scene header row + visibility toggle + unload button |
| 7 | **World header** (name, path, game mode, settings) | Low | Just UI layout + click handler |
| 8 | **Editor scene toggle** | Low | Boolean flag + conditional section |
| 9 | **Dirty tracking** integration | Low | Port `MarkSceneHierarchyDirty` logic |
| 10 | **Asset drop handling** (prefab/model from asset browser) | Medium | Need native asset payload type in `EditorDragDropUtility` |
| 11 | **EditorStyles expansion** | Low | Add selected-row, hover, hierarchy-specific colors |
| 12 | **`UITreeTransform`/`UITreeItemTransform`** stubs | Medium | Currently empty — could flesh out for proper tree layout, or keep flat-list approach |

### Critical Path

The biggest blockers are **expand/collapse** (#1) and **context menu** (#3) — these are the most complex to build and are the features that make the hierarchy feel like a real tree editor vs. a flat list. Everything else layers on incrementally.

---

## Phase 1: Foundation Infrastructure (3 new files / changes)

Reusable components needed by the hierarchy but useful across the editor.

### 1A. Right-Click + Modifier Key Input

**File:** `XRENGINE/Scene/Components/Pawns/UICanvasInputComponent.cs`

**Problem:** `UICanvasInputComponent.RegisterInput()` only registers `LeftClick`, `MouseMove`, `MouseScroll`. No right-click, no modifier key tracking.

**Changes:**

- Register `EMouseButton.Right` press/release → fire new `RightClick` on `TopMostInteractable`
- Track modifier key state: add `bool IsCtrlHeld`, `IsShiftHeld`, `IsAltHeld` properties updated via `EKey.ControlLeft/Right`, `EKey.ShiftLeft/Right`, `EKey.AltLeft/Right` state change registrations
- Expose these on `UICanvasInputComponent` so any interactable can query them

**Impact:** ~40 lines added.

### 1B. Native Context Menu Component

**New file:** `XRENGINE/Scene/Components/UI/Interactable/UIContextMenuComponent.cs`

**Design:**

```
UIContextMenuComponent : UIComponent, requires UIBoundableTransform
├── MenuItem[] Items  { Label, Action, Enabled }
├── Show(Vector2 canvasPosition)  — creates child nodes for each item
├── Hide()  — destroys child nodes, removes from scene
├── Auto-dismiss on click outside or Escape
└── Renders as vertical list of UIButtonComponents with hover highlight
```

**Key decisions:**

- Spawned as a child of the canvas root (top z-order) at cursor position
- Each menu item is a `UIButtonComponent` with text label
- Dismiss via: click on item, click outside bounds, Escape key, or scroll
- Styling via `EditorStyles` constants (dark background, light text, highlight on hover)

**Estimated:** ~120 lines.

### 1C. Single-Line Text Input Mode

**File:** `XRENGINE/Scene/Components/UI/Core/Interactable/UITextInputComponent.cs`

**Changes:**

- Add `bool SingleLineMode { get; set; }` property
- When `SingleLineMode = true`:
  - Enter key → fire new `Submitted` event instead of inserting newline
  - Register `EKey.Escape` → fire new `Cancelled` event
- Add events: `event Action<UITextInputComponent>? Submitted`, `Cancelled`

**Impact:** ~25 lines added.

---

## Phase 2: Expand/Collapse Tree Structure ✅

### 2A. Collapse State Tracking

**File:** `XREngine.Editor/UI/Panels/HierarchyPanel.cs`

**Add:**

```csharp
private readonly HashSet<Guid> _collapsedNodes = new();

bool IsCollapsed(SceneNode node) => _collapsedNodes.Contains(node.ID);
void ToggleCollapse(SceneNode node) {
    if (!_collapsedNodes.Remove(node.ID))
        _collapsedNodes.Add(node.ID);
    RemakeChildren();  // rebuild flat list respecting collapsed state
}
```

### 2B. Expand Arrow Button

**In `CreateNodes()`** — add an arrow/triangle button before each non-leaf node:

- Leaf nodes: no arrow (indent spacer only)
- Branch nodes (has children): clickable `▶` / `▼` toggle button
- Arrow button width: ~16px, positioned at `depth * DepthIncrement` offset
- Text label shifted right by arrow width
- Click on arrow → `ToggleCollapse(node)`, does NOT change selection

### 2C. Skip Collapsed Children

**In `CreateNodes()`** — after rendering a node, only recurse into children if `!IsCollapsed(node)`:

```csharp
if (!IsCollapsed(node))
    CreateNodes(listNode, node.Transform.Children.Select(...), ref renderedCount, maxToRender);
```

Also update `CountNodes()` to respect collapsed state for accurate scroll metrics.

**Estimated:** ~60 lines changed/added in `HierarchyPanel.cs`.

---

## Phase 3: Multi-Selection ✅

**File:** `XREngine.Editor/UI/Panels/HierarchyPanel.cs`

**Changes to `HandleNodeButtonInteraction()`:**

- Query `UICanvasInputComponent` modifier state (from Phase 1A)
- Ctrl+Click → toggle in `Selection.SceneNodes` array
- Shift+Click → add to selection
- Alt+Click → remove from selection
- Plain click → single-select (existing behavior, but use `Selection.SceneNodes = [node]`)
- Update visual highlight: selected nodes get a highlight background color

**Add `UpdateNodeHighlights()` method:**

- Subscribe to `Selection.SelectionChanged`
- Walk all `NodeWrapper` children, set `Highlighted = true/false` based on `Selection.SceneNodes.Contains(node)`
- `NodeWrapper.OnPropertyChanged(Highlighted)` updates the background material color

**Estimated:** ~50 lines.

---

## Phase 4: Context Menu ✅

**File:** `XREngine.Editor/UI/Panels/HierarchyPanel.cs`

**Requires:** Phase 1A (right-click), Phase 1B (context menu component)

**Changes:**

- On right-click a node button → show context menu with items:
  - **Rename** → starts inline rename (Phase 5)
  - **Delete** → port `DeleteHierarchyNode` logic from ImGui version
  - **Add Child Scene Node** → port `CreateChildSceneNode`
  - **Focus Camera** → already exists as double-click action; just call `TryFocusCameraOnNode()`
- All mutations via `EnqueueSceneEdit()`

**Estimated:** ~40 lines in HierarchyPanel + Phase 1B component.

---

## Phase 5: Inline Rename ✅

**File:** `XREngine.Editor/UI/Panels/HierarchyPanel.cs`

**Requires:** Phase 1C (single-line text input), Phase 4 (context menu "Rename" trigger)

**Changes:**

- Track `_nodePendingRename: SceneNode?`
- When rename starts: replace the `UITextComponent` label with a `UITextInputComponent` (single-line mode)
  - Pre-fill with current name, auto-select all text
  - `Submitted` → apply new name via `EnqueueSceneEdit`, call `MarkSceneHierarchyDirty`
  - `Cancelled` → revert to text label
- On rename end: destroy the input, restore the label
- Alternative: keep a pooled rename input and just show/hide + reparent it

**Estimated:** ~60 lines.

---

## Phase 6: Active/Enabled Toggle ✅

**File:** `XREngine.Editor/UI/Panels/HierarchyPanel.cs`

**Changes to `CreateNodes()`:**

- After the text label, add a `NativeUIElements.CreateCheckboxToggle()` bound to `node.IsActiveSelf`
- Position it right-aligned within the row (similar to ImGui 72px fixed column)
- On toggle → `EnqueueSceneEdit(() => node.IsActiveSelf = value)` + `MarkSceneHierarchyDirty`

**Estimated:** ~20 lines.

---

## Phase 7: Scene-Grouped Sections + World Header

**File:** `XREngine.Editor/UI/Panels/HierarchyPanel.cs`

### 7A. World Header ✅

Add a non-scrolling header section above the tree list:

- World name + file path text
- Game mode name text
- "Settings" button → opens world settings in inspector
- "Show Editor Scene" checkbox

**Estimated:** ~40 lines.

### 7B. Scene Sections ✅

Change `CreateTree()` to group nodes by scene:

- For each scene in `world.TargetWorld.Scenes` (excluding editor-only):
  - Render a collapsible section header: scene name + dirty indicator (`*`)
  - Visibility checkbox + "Unload" button on the right
  - Scene file path as tooltip (would need hover handler)
  - Only render child nodes if section is expanded
- After all scenes: "World Root Nodes" section for unassigned roots
- Optionally: "Editor Scene (Hidden)" section when debug toggle is on

**Port from:** ImGui `DrawSceneHierarchySection`, `DrawUnassignedHierarchy`, `DrawRuntimeHierarchy`

**Estimated:** ~80 lines.

---

## Phase 8: Dirty Tracking + Asset Drop Handling

### 8A. Dirty Tracking ✅

Port `MarkSceneHierarchyDirty()`, `FindSceneForNode()`, `GetHierarchyRoot()` from the ImGui version. These are pure logic methods — direct copy with minor adjustments.

**Estimated:** ~40 lines.

### 8B. Asset Drop from Asset Browser ✅

**Requires:** Extending `EditorDragDropUtility` with an asset path payload type (currently only supports `SceneNode`).

**Changes:**

- Add `CreateAssetPayload(string path)` / `TryGetAssetPath(payload, out string)` to `EditorDragDropUtility`
- Register the hierarchy's list transform as a drop target for asset payloads
- On drop: port `HandleHierarchyModelAssetDrop` logic (prefab/model spawning)
- This also requires the native asset explorer to initiate drags with the new payload type

**Estimated:** ~50 lines across 2 files.

---

## Phase 9: EditorStyles Expansion ✅

**File:** `XREngine.Editor/UI/EditorStyles.cs`

Add constants for:

```csharp
// Hierarchy tree
static readonly ColorF4 SelectedRowColor = new(0.25f, 0.55f, 0.95f, 0.35f);
static readonly ColorF4 HoverRowColor = new(1f, 1f, 1f, 0.08f);
static readonly ColorF4 ExpandArrowColor = ColorF4.White;
static readonly ColorF4 ContextMenuBackground = new(0.15f, 0.15f, 0.15f, 0.95f);
static readonly ColorF4 ContextMenuHover = new(0.3f, 0.5f, 0.8f, 0.6f);
static readonly float HierarchyFontSize = 14f;
static readonly float HierarchyRowHeight = 30f;
static readonly float DepthIndent = 10f;
```

**Estimated:** ~20 lines.

---

## Recommended Implementation Order

```
Phase 1A: Right-click + modifier keys in UICanvasInputComponent     ← unblocks 3, 4
Phase 1C: Single-line text input mode                                ← unblocks 5
Phase 9:  EditorStyles expansion                                     ← used by all phases
Phase 2:  Expand/collapse tree structure                             ← biggest visual impact
Phase 6:  Active/enabled toggle checkbox                             ← quick win
Phase 3:  Multi-selection                                            ← uses Phase 1A
Phase 1B: Native context menu component                              ← unblocks 4
Phase 4:  Context menu wiring                                        ← uses 1A + 1B
Phase 5:  Inline rename                                              ← uses 1C + 4
Phase 7:  Scene sections + world header                              ← structural change
Phase 8A: Dirty tracking                                             ← logic port
Phase 8B: Asset drop handling                                        ← depends on asset explorer
```

---

## Estimated Total Effort

~600 lines of new/modified code across 5–6 files. The native panel would grow from 547 → ~900 lines, plus ~140 lines of new reusable components (`UIContextMenuComponent`, input changes).

---

## Files Touched

| File | Action |
|------|--------|
| `XRENGINE/Scene/Components/Pawns/UICanvasInputComponent.cs` | Add right-click, modifier tracking |
| `XRENGINE/Scene/Components/UI/Core/Interactable/UITextInputComponent.cs` | Add single-line mode, Submitted/Cancelled events |
| **New:** `XRENGINE/Scene/Components/UI/Interactable/UIContextMenuComponent.cs` | Native popup context menu |
| `XREngine.Editor/UI/Panels/HierarchyPanel.cs` | Expand/collapse, multi-select, context menu, rename, active toggle, scene sections, world header, dirty tracking |
| `XREngine.Editor/UI/EditorDragDropUtility.cs` | Asset path payload type |
| `XREngine.Editor/UI/EditorStyles.cs` | Hierarchy-specific style constants |
