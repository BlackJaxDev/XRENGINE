# 04 — Known Issues & Architectural Concerns

This document catalogs known architectural problems, performance traps, and bugs in the native UI layout system. Each issue includes analysis, affected code, and potential fixes. Last updated from a comprehensive codebase audit.

> **Scope**: This document covers only the native UI rendering and layout paths (`UILayoutSystem`, `UIBoundableTransform`, etc.). ImGui overlay code paths are explicitly excluded.

---

## Status of Previously Documented Issues

Several issues from the prior version of this document have been resolved:

| Former Issue | Status | Notes |
|---|---|---|
| Dual Layout Path Conflict | **RESOLVED** | The OLD path call site in `UIBoundableTransform.OnLocalMatrixChanged` has been removed. The comment at [UIBoundableTransform.cs](XRENGINE/Scene/Components/UI/Core/Transforms/UIBoundableTransform.cs) line ~491 confirms: *"OLD layout path removed: OnResizeChildComponents is no longer called here."* Layout is now solely driven by `UILayoutSystem` via `ArrangeChildren`. The `OnResizeChildComponents` overrides in all transform classes are dead code. |
| `SetField` Uses `ReferenceEquals` | **RESOLVED** | All `SetField<T>` overloads in `XRBase.cs` now use `EqualityComparer<T>.Default.Equals(field, value)`. No `ReferenceEquals` calls remain. The mitigation in `OnResizeActual` (checking value changes before setting properties) is still in place as a defense-in-depth measure. |
| Grid/Tab Missing `ArrangeChildren` | **PARTIALLY RESOLVED** | `UIGridTransform` now has both `ArrangeChildren` (new path, line ~163) and `OnResizeChildComponents` (dead code, line ~314). No `UITabTransform` class exists in the codebase — it was either renamed or removed. |
| Empty `UITransform.FitLayout` | **STILL PRESENT** | `UITransform.FitLayout` remains empty. However, the legacy `FitLayout` bridge (`UILayoutSystem.FitLayout`) now calls `MeasureBoundable` + `ArrangeBoundable`, so only `UIBoundableTransform` children get layout. Plain `UITransform` children (e.g., `UIRotationTransform`) still receive no layout. |

The remaining issues below were **active bugs** affecting the editor. Issues 1–3 have been fixed as of this update.

---

## Issue 1: Measure Phase Does Not Recurse Into Descendants — FIXED

### Symptom

**Menu bar items are all stuck at position 0** — all horizontal list items overlap on the left instead of being laid out side-by-side with their text widths.

### Root Cause

The synchronous measure phase in `UILayoutSystem.UpdateCanvasLayout` stops at any transform with explicit `Width`/`Height`:

```
UILayoutSystem.UpdateCanvasLayout:
  Phase 1: MeasureTransform(canvas, bounds.Extents)
    → MeasureBoundable(canvas, ...)
      → canvas.Width.HasValue == true  →  desiredWidth = Width.Value
      → canvas.Height.HasValue == true →  desiredHeight = Height.Value
      → DesiredSize set. DONE. Children never measured.
  Phase 2: ArrangeTransform(canvas, bounds)
    → Recurses top-down into ArrangeChildren...
      → UIListTransform.ArrangeChildrenLeftTop:
         float size = ItemSize ?? bc.DesiredSize.X;  // DesiredSize.X == 0!
```

Because the canvas has explicit Width/Height (set by `UICanvasTransform.SetSize()` from viewport size), `MeasureBoundable` takes the early-return path. It never calls `InvokeMeasureChildrenWidth`, so no descendant's `Measure()` is ever invoked. Every descendant's `DesiredSize` remains `Vector2.Zero`.

### Affected Code

- [UILayoutSystem.cs](XRENGINE/Scene/Components/UI/Core/Transforms/UILayoutSystem.cs) `MeasureBoundable` (line ~207): explicit Width/Height short-circuits, children not measured
- [UIListTransform.cs](XRENGINE/Scene/Components/UI/Core/Arrangements/UIListTransform.cs) `ArrangeChildrenLeftTop` (line ~209): reads `bc.DesiredSize.X` which is 0
- Same issue in `ArrangeChildrenCentered`, `ArrangeChildrenRightBottom`, `CalculateTotalChildSize`
- Also affects `MeasureChildrenWidth`/`MeasureChildrenHeight` overrides in `UIListTransform` (lines ~120–170) — they call `bc.Measure()` conditionally via `bc.NeedsMeasure`, but `NeedsMeasure` was never set to true because the measure phase never reached them

### Why the OLD Path Worked

The legacy `SizeChildrenLeftTop` method used `bc.ActualWidth` / `bc.ActualHeight` (from `GetActualBounds` → `GetWidth()` → `CalcAutoWidthCallback`), which correctly chains through to `UITextComponent.CalcAutoWidth()` for text measurement. The NEW path reads `bc.DesiredSize` which is only populated by the measure phase.

### Impact

- **Horizontal lists with `ItemSize = null`**: All items get size 0, stacked at x=0 (the menu bar)
- **Vertical lists with `ItemSize = null`**: All items get size 0 (potential issue for auto-height lists)
- Any layout that depends on child `DesiredSize` for sizing decisions

### Applied Fix

**Option B (systemic)** was applied: `UILayoutSystem.MeasureBoundable` now always calls `MeasureChildren(transform, availableSize)` after computing the parent's desired size. This ensures all children have their `DesiredSize` populated regardless of whether the parent has explicit Width/Height. The `NeedsMeasure` + `LastMeasureConstraint` guard prevents redundant work when children were already measured by the auto-sizing path.

Changed in: [UILayoutSystem.cs](XRENGINE/Scene/Components/UI/Core/Transforms/UILayoutSystem.cs) `MeasureBoundable`

---

## Issue 2: `IsHoveringUI()` Always Returns True — Blocks All Camera Input — FIXED

### Symptom

**No keyboard or mouse inputs move the flying editor camera**. Right-click drag rotation, WASD movement, and scroll zoom are all non-functional when the native UI canvas is active.

### Root Cause

The camera pawn checks `IsHoveringUI()` before allowing mouse-based camera interaction:

```csharp
// FlyingCameraPawnBaseComponent.cs line ~217
public bool IsHoveringUI()
    => LinkedUICanvasInputs.Any(x => x.TopMostElement is not null);
```

`TopMostElement` is set during `UICanvasInputComponent.SwapBuffers()`:
```csharp
TopMostElement = UIElementIntersections.FirstOrDefault(
    x => x.Owner is UIComponent)?.Owner as UIComponent;
```

The quadtree query (`FindAllIntersectingSorted`) uses `UIElementPredicate`:
```csharp
protected static bool UIElementPredicate(RenderInfo2D item)
    => item.Owner is UIComponent ui && ui.UITransform.IsVisibleInHierarchy;
```

**The problem**: The native UI canvas covers the **entire viewport**. Background panels, container elements (the `UIDualSplitTransform` root, `UIMultiSplitTransform` dock area, `UIMaterialComponent` backgrounds), and other non-interactive elements all have `RenderInfo2D` entries in the quadtree spanning the full viewport. The cursor is *always* over at least one `UIComponent`, so `TopMostElement` is **never null**, and `IsHoveringUI()` **always returns true**.

This cascades to block:

| Blocked Action | Gate | Location |
|---|---|---|
| Right-click drag start | `_rightClickDragging = !IsHoveringUI()` → always false | `FlyingCameraPawnBaseComponent.OnRightClick` |
| Camera rotation/translation | `Rotating`/`Translating` check `_rightClickDragging` → always false | Multiple methods |
| Left-click selection | `if (IsHoveringUI()) return;` | `EditorFlyingCameraPawnComponent` line ~1548 |
| Scroll zoom | `if (IsHoveringUI()) return;` | `EditorFlyingCameraPawnComponent` line ~1637 |
| Depth query on right-click | `if (_rightClickPressed && !IsHoveringUI())` | `EditorFlyingCameraPawnComponent` line ~1438 |

Additionally, `AllowKeyboardInput` is a separate gate:
```csharp
public bool AllowKeyboardInput
    => LocalPlayerController?.FocusedUIComponent is null;
```
This blocks WASD/arrow keys when any `UIInteractableComponent` has focus. Right-clicking in the viewport is supposed to clear `FocusedUIComponent`, but since `_rightClickDragging` is never set (because `IsHoveringUI()` is always true), the focus-clearing code never executes.

### Affected Code

- [FlyingCameraPawnBaseComponent.cs](XRENGINE/Scene/Components/Pawns/FlyingCameraPawnBaseComponent.cs) `IsHoveringUI()` (line ~217)
- [UICanvasInputComponent.cs](XRENGINE/Scene/Components/Pawns/UICanvasInputComponent.cs) `UIElementPredicate` (line ~467), `SwapBuffers` (line ~286)
- [EditorFlyingCameraPawnComponent.cs](XREngine.Editor/EditorFlyingCameraPawnComponent.cs) — all guarded input handlers

### Applied Fix

**Option A** was applied: `IsHoveringUI()` now checks `TopMostInteractable` instead of `TopMostElement`. Background panels and container transforms no longer block camera input — only actual interactive elements (buttons, text fields, etc.) do.

Changed in: [FlyingCameraPawnBaseComponent.cs](XRENGINE/Scene/Components/Pawns/FlyingCameraPawnBaseComponent.cs) `IsHoveringUI()`

---

## Issue 3: Canvas Layout Not Fully Invalidated on Height-Only Window Resize — FIXED

### Symptom

**The top menu bar aligns correctly to the top of the window when resizing width, but not when resizing only height.** The menu bar appears to stay at a stale Y position until a width change forces a full re-layout.

### Root Cause Analysis

The resize chain works correctly up to the point of invalidation:

1. Window resize → `XRViewport.Resize()` → calls `ResizeCameraComponentUI()` and fires `Resized` event
2. `CameraComponent.ViewportResized` → `canvasTransform.SetSize(viewport.Region.Size)`
3. `SetSize` sets `Width` then `Height`. For height-only resize, `Width` setter is a no-op (same value), `Height` setter fires `OnPropertyChanged` → `InvalidateMeasure()` → `InvalidateCanvasLayout()` → `InvalidateChildrenRecursive()`

The invalidation appears correct — all children get `ForceInvalidateArrange()`. However, the editor uses a vertical `UIDualSplitTransform` where the menu bar (first child) is positioned at:
```csharp
// UIDualSplitTransform.ArrangeChildren, vertical split:
float bottomSize = paddedRegion.Height - fixedSize;  // Changes with height!
FitLayout(a, new(paddedRegion.X, paddedRegion.Y + bottomSize + SplitterSize, ...));
```

The menu bar's Y position changes with every height change (it's placed at `bottomSize + splitterSize` from the bottom). The **bounds passed to `ArrangeBoundable`** are different. The guard in `ArrangeBoundable`:
```csharp
if (!transform.NeedsArrange && transform.LastArrangeBounds.Equals(finalBounds))
    return;
```
should allow this through because `NeedsArrange` was set to true by `ForceInvalidateArrange()`.

**Suspected cause**: A race condition or version counter issue where `NeedsArrange` gets cleared before the arrange phase reaches the affected children. This could happen if:
- `SetSize` triggers `InvalidateCanvasLayout` which calls `InvalidateChildrenRecursive`, but then the `UpdateLayout` call in the same frame's `UpdateFrame` handler has already started and the version check gets confused
- The async layout coroutine's `ArrangeChildCoroutine` has a weaker guard (`if (!child.NeedsArrange) yield break;`) that doesn't check if bounds changed — if async layout is enabled, this skip guard would prevent re-arrangement when `NeedsArrange` is coincidentally false

### Affected Code

- [UICanvasTransform.cs](XRENGINE/Scene/Components/UI/Core/Transforms/UICanvasTransform.cs) `SetSize` (line ~280)
- [UILayoutSystem.cs](XRENGINE/Scene/Components/UI/Core/Transforms/UILayoutSystem.cs) `ArrangeBoundable` skip guard (line ~332), `ArrangeChildCoroutine` skip guard (line ~716)
- [UIDualSplitTransform.cs](XRENGINE/Scene/Components/UI/Core/Transforms/UIDualSplitTransform.cs) vertical split Y-position calculation (line ~114)
- [UICanvasComponent.cs](XRENGINE/Scene/Components/Pawns/UICanvasComponent.cs) `UpdateLayout` (line ~158) — timing of layout relative to resize events

### Root Cause (Confirmed)

The root cause was a **PlacementInfo disconnect**: when the `UIDualSplitTransform` recalculates its vertical split on height change, it updates `PlacementInfo.Offset` on the menu bar child to the new Y position. However, changing `Offset` did NOT set `RelativePositioningChanged = true` (the flag was never wired to subclass property changes). Meanwhile, `OnResizeActual` only checked if `ActualLocalBottomLeftTranslation` or `ActualSize` changed — for stretched children these are always (0,0) relative to the sub-region, so no change was detected, and `MarkLocalModified` was never called.

### Applied Fix (Three-part)

1. **`UIChildPlacementInfo.OnPropertyChanged`**: Added override in the base class that sets `RelativePositioningChanged = true` whenever any positioning property changes (Offset, BottomOrLeftOffset, etc.). This automatically applies to all subclasses (`UISplitChildPlacementInfo`, `UIListChildPlacementInfo`, `UIGridChildPlacementInfo`, etc.).

2. **`UIBoundableTransform.OnResizeActual`**: Expanded the dirty check to also call `ShouldMarkLocalMatrixChanged()`, which detects `PlacementInfo.RelativePositioningChanged`. Now offset changes correctly trigger `MarkLocalModified`.

3. **`UILayoutSystem.ArrangeChildCoroutine`**: Added bounds check to the async skip guard (matching the synchronous `ArrangeBoundable` dual guard) so children are re-arranged when their bounds change even if `NeedsArrange` was cleared.

Changed in:
- [UIChildPlacementInfo.cs](XRENGINE/Scene/Components/UI/Core/Transforms/UIChildPlacementInfo.cs)
- [UIBoundableTransform.cs](XRENGINE/Scene/Components/UI/Core/Transforms/UIBoundableTransform.cs) `OnResizeActual`
- [UILayoutSystem.cs](XRENGINE/Scene/Components/UI/Core/Transforms/UILayoutSystem.cs) `ArrangeChildCoroutine`

---

## Issue 4: Scene Node Hierarchy Not Top-Aligned Within Boundary (MEDIUM)

### Symptom

**The scene node hierarchy panel doesn't align all nodes to the top of its boundary correctly.** Nodes may appear vertically centered or offset rather than starting from the top.

### Root Cause Analysis

The hierarchy panel creates a vertical `UIListTransform`:
```csharp
// HierarchyPanel.CreateTree:
listTfm.DisplayHorizontal = false;
listTfm.ItemAlignment = EListAlignment.TopOrLeft;
listTfm.ItemSize = ItemHeight;  // 30px per item
listTfm.Width = 150;            // Fixed width
listTfm.Height = null;          // Auto-height
```

With `Height = null`, the list auto-sizes to fit its content. But it's placed inside the "Hierarchy" node which is the first child of a `UIMultiSplitTransform` with `LeftMiddleRight` arrangement:
```csharp
dockTfm.FixedSizeFirst = 300;  // 300px wide for hierarchy
```

The hierarchy slot is 300px wide and fills the full height of the dock area. The `UIListTransform` with `Height = null` auto-sizes to `totalItems * ItemHeight`. If there are fewer items than fill the height, the list's actual height is smaller than the parent slot.

This connects to **Issue 1** (measure not recursing): if the list's `DesiredSize` is never properly calculated (because measure doesn't reach it), the parent's `ArrangeBoundable` may pass incorrect bounds. Additionally, since `ItemSize = 30` is a fixed value, the list items DO get correct sizes — but the list container itself may have a stale `ActualSize`.

**Secondary issue**: The list node has `Width = 150` but the parent slot is 300px. The default anchors (0,0)→(1,1) would stretch the list to 300px, but the explicit `Width = 150` overrides this for point anchors. However, anchors are (0,0)→(1,1) by default, making this a stretched anchor, meaning the explicit Width is used as an offset from the max anchor — resulting in unexpected sizing.

The vertical alignment issue is caused by the `ArrangeChildrenLeftTop` method starting `y` at `parentRegion.Height` and subtracting downward:
```csharp
float y = _horizontal ? 0 : parentRegion.Height;
// ...
y -= size;
ArrangeChildVertical(bc, x, y, size, parentRegion.Width);
```

If `parentRegion.Height` is incorrect (due to stale bounds or the auto-height not propagating correctly), the starting Y position is wrong, causing items to not align to the visual top of the container.

### Affected Code

- [HierarchyPanel.cs](XREngine.Editor/UI/Panels/HierarchyPanel.cs) `CreateTree` (line ~118): list setup with `Height = null`, `Width = 150`
- [UIListTransform.cs](XRENGINE/Scene/Components/UI/Core/Arrangements/UIListTransform.cs) `ArrangeChildrenLeftTop` (line ~201): vertical start position depends on `parentRegion.Height`
- Interaction with Issue 1: `parentRegion` for the list comes from the parent's `ArrangeChildren` which may use stale `DesiredSize`

### Potential Fixes

**Fix A — Ensure list auto-height is correctly measured** (depends on fixing Issue 1):
Once the measure phase properly recurses, the list's `DesiredSize.Y` will be `totalItems * ItemHeight`, and its parent can pass correct bounds.

**Fix B — Set `Height` explicitly based on content**:
```csharp
listTfm.Height = nodes.Count * ItemHeight;
```
Avoids relying on auto-sizing altogether.

**Fix C — Set appropriate anchors**: The list should probably anchor to the top-left of its parent and auto-size downward:
```csharp
listTfm.MinAnchor = new Vector2(0.0f, 1.0f);  // top-left
listTfm.MaxAnchor = new Vector2(1.0f, 1.0f);   // top-right
// Width stretches to parent, height is auto
listTfm.Width = null;
listTfm.Height = null;
```

---

## Issue 5: PlacementInfo Offset + Layout Position — Potential Double Positioning (MEDIUM)

### Description

When `ArrangeChildren` in split/dock transforms calls `UILayoutSystem.FitLayout(child, subRegion)`, the `subRegion` already specifies the target position (e.g., `x = leftSize + splitterSize`). But the parent also sets `PlacementInfo.Offset` to the same value:

```csharp
// UIDualSplitTransform.ArrangeChildren:
if (b.PlacementInfo is UISplitChildPlacementInfo bInfo)
    bInfo.Offset = leftSize + SplitterSize;
UILayoutSystem.FitLayout(b, new(paddedRegion.X + leftSize + SplitterSize, ...));
```

In `CreateLocalMatrix`:
```csharp
Matrix4x4 mtx = Matrix4x4.CreateTranslation(new Vector3(ActualLocalBottomLeftTranslation, DepthTranslation));
var p = PlacementInfo;
if (p is not null)
    mtx *= p.GetRelativeItemMatrix();  // Adds offset from PlacementInfo
```

### Impact

With default stretched anchors (0,0)→(1,1) and the sub-region passed as `parentBounds`, `GetActualBounds` computes `bottomLeftTranslation = 0` relative to the sub-region. Then `PlacementInfo.Offset` translates from the parent's origin to the sub-region's position. This works correctly for the common case.

However, for children with **non-default anchors** inside a split panel, `GetActualBounds` computes a non-zero translation based on the sub-region's size and anchor position. The `PlacementInfo.Offset` then adds an additional translation, which may result in double-positioning.

### Status

This issue appears to be latent — the current editor uses only stretched anchors inside split panels. It would manifest if custom UI configurations use point anchors inside split/dock transforms.

### Recommendation

Audit whether `PlacementInfo.Offset` should be set at all when the sub-region already encodes the position. Consider removing `PlacementInfo` for split/dock children since the sub-region passed to `FitLayout` already contains the full positional information.

---

## Issue 6: `OnResizeChildComponents` Is Dead Code Throughout Codebase (LOW)

### Description

The OLD layout path entry point in `UIBoundableTransform.OnLocalMatrixChanged` has been removed. The `OnResizeChildComponents` virtual method and all its overrides are now dead code:

| Class | Location |
|---|---|
| `UIListTransform` | `OnResizeChildComponents` + `SizeChildrenLeftTop/Centered/RightBottom` |
| `UIGridTransform` | `OnResizeChildComponents` (full grid layout logic) |
| `UIDualSplitTransform` | `OnResizeChildComponents` (split layout logic) |
| `UIMultiSplitTransform` | `OnResizeChildComponents` (multi-split logic) |
| `UIDockingRootTransform` | `OnResizeChildComponents` |
| `UIDockableTransform1` | `OnResizeChildComponents` |
| `UIScrollableTransform` | `OnResizeChildComponents` |
| `UIScrollingTransform` | `OnResizeChildComponents` |

### Impact

No runtime impact — this is dead code that adds maintenance burden and confusion. It serves as a reference for the original layout logic but could mislead developers into thinking both paths are active.

### Recommendation

Either remove all `OnResizeChildComponents` overrides and the base virtual method, or mark them with `[Obsolete]` and add comments indicating they are preserved only for reference.

---

## Issue 7: Auto-Sizing Circular Dependencies (LOW)

### Description

When a parent has `Width = null` (auto-size), it measures by calling `GetMaxChildWidth()` which queries children. If a child has stretched anchors (0,0)→(1,1), its size depends on the parent size. This creates a circular dependency.

### Current Behavior

The layout system works correctly when:
- Parent auto-sizes + children have explicit `Width`/`Height` or point anchors
- Parent has explicit size + children use stretched anchors

**Mixing auto-sizing parents with stretched-anchor children is unsupported** and may produce size 0.

### Status

This is a design constraint, not a bug. Document it as a usage requirement.

---

## Issue 8: `UITransform.FitLayout` Remains Empty for Non-Boundable Transforms (LOW)

### Description

`UITransform.FitLayout(BoundingRectangleF)` is an empty virtual method. Only `UIBoundableTransform.FitLayout` has an implementation (delegating to `UILayoutSystem.FitLayout`).

If a child is a plain `UITransform` (e.g., `UIRotationTransform`) and the parent's layout calls `UILayoutSystem.FitLayout`, the system only handles `UIBoundableTransform` children. Plain `UITransform` children in the `ArrangeChildrenBoundable` fallback do get `ArrangeTransform` called, but custom layout transforms that call `FitLayout` directly on children will silently skip non-boundable transforms.

### Impact

Minimal in current editor — all UI elements use `UIBoundableTransform`. Could affect future custom UI configurations using `UIRotationTransform` as an intermediate node.

---

## Issue 9: Thread Safety Concerns with Commented-Out Locks (LOW)

### Description

Several collections are iterated without synchronization, with commented-out `lock` statements throughout:

```csharp
// Seen in UIBoundableTransform, UIListTransform, etc.:
//lock (Children)
//{
    foreach (var c in Children)
        ...
//}
```

### Impact

Layout runs on the main thread during `UpdateFrame`. As long as UI hierarchy modifications also happen on the main thread, this is safe. If async layout (`UseAsyncLayout`) is ever enabled, this becomes a real concern — `InvalidOperationException` (collection modified during enumeration) can occur.

### Status

Not currently a problem since async layout is not active in the editor. Would need addressing if async layout is enabled.

---

## Issue 10: `UIScrollingTransform` Is a Stub (LOW)

### Description

[UIScrollingTransform.cs](XREngine.Editor/UI/UIScrollingTransform.cs) in the editor has placeholder properties (`ScrollPosition`, `ScrollSize`) but no functional implementation. The `GetActualBounds` override just delegates to base, and `OnResizeChildComponents` has a TODO comment about clipping.

### Impact

Scroll regions in the editor (e.g., long hierarchy lists, inspector panels) won't clip or scroll content. Currently mitigated by the hierarchy panel's truncation system (`MaxNodesToRender = 2000`).

---

## Summary: Priority Matrix

| Issue | Severity | Symptom | Fix Difficulty |
|---|---|---|---|
| ~~1. Measure phase doesn't recurse~~ | ~~Critical~~ | ~~Menu bar items overlap at x=0~~ | **FIXED** |
| **2. Camera input blocked** | **Critical** | No WASD/mouse camera movement | **Active** |
| ~~3. Height-only resize layout stale~~ | ~~High~~ | ~~Menu bar misaligned after height resize~~ | **FIXED** |
| ~~4. Hierarchy top-alignment~~ | ~~Medium~~ | ~~Scene nodes not top-aligned~~ | **FIXED** (by #1) |
| 5. PlacementInfo double offset | Medium | Latent — non-default anchors in splits | Medium |
| 6. Dead `OnResizeChildComponents` code | Low | Maintenance burden | Easy (cleanup) |
| 7. Auto-size circular dependencies | Low | Design constraint | N/A |
| 8. Empty `UITransform.FitLayout` | Low | Non-boundable children get no layout | Easy |
| 9. Thread safety / commented locks | Low | Only if async layout enabled | Medium |
| 10. `UIScrollingTransform` is a stub | Low | No scroll/clip functionality | Medium-Hard |
