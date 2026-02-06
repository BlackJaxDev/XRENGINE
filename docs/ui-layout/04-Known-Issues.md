# 04 — Known Issues & Architectural Concerns

This document catalogs known architectural problems, performance traps, and bugs in the UI layout system. Each issue includes analysis, affected code, and potential fixes.

---

## Issue 1: Dual Layout Path Conflict (CRITICAL)

### Description

Two independent layout paths execute for every UI transform:

| Path | Trigger | Entry Point | When |
|------|---------|-------------|------|
| **NEW** | `CollectVisible` → `UpdateLayout()` | `UILayoutSystem.UpdateCanvasLayout` → `ArrangeTransform` → virtual `ArrangeChildren` | During render collection |
| **OLD** | `SwapBuffers` → `RecalculateMatrices` → `CreateLocalMatrix` | `OnLocalMatrixChanged` → `OnResizeChildComponents` → `FitLayout` on children | After render collection, during buffer swap |

### Problem

1. **Double work**: Every layout change runs measure+arrange twice — once via the NEW path, then again via the OLD path when deferred matrix recalculation triggers `OnLocalMatrixChanged`.

2. **Stale positions on first frame**: The NEW path runs during `CollectVisible`, but `MarkLocalModified(deferred: true)` defers the actual matrix update to `SwapBuffers`. The OLD path then re-lays out children with correct matrices, but this happens after collection — so the quadtree may see stale positions for one frame.

3. **Ordering conflict**: The NEW path calls `ArrangeChildren` top-down. The OLD path calls `OnResizeChildComponents` via `OnLocalMatrixChanged` which fires bottom-up as each transform's deferred matrix gets recalculated. If the order differs, layout results can be inconsistent.

### Affected Code

- `UIBoundableTransform.OnLocalMatrixChanged` (line ~632 in UIBoundableTransform.cs):
  ```csharp
  OnResizeChildComponents(ApplyPadding(GetActualBounds()));
  ```
- `UIBoundableTransform.OnResizeActual` calling `MarkLocalModified(deferred: true)`

### Potential Fix

**Option A — Remove OLD path entirely**: Delete the `OnLocalMatrixChanged` → `OnResizeChildComponents` call. Ensure the NEW path handles ALL layout. This requires:
- Adding `ArrangeChildren` overrides to `UIGridTransform` and `UITabTransform` (currently only have `OnResizeChildComponents`)
- Converting all remaining `OnResizeChildComponents` callers to use the NEW path
- Verifying that deferred matrix recalculation + render matrix swap still works correctly for quadtree positioning

**Option B — Skip OLD path during layout**: Add a guard:
```csharp
protected override void OnLocalMatrixChanged(Matrix4x4 localMatrix)
{
    base.OnLocalMatrixChanged(localMatrix);
    if (!ParentCanvas?.IsUpdatingLayout ?? true)
        OnResizeChildComponents(ApplyPadding(GetActualBounds()));
}
```
This prevents the OLD path from running when the NEW path is driving layout. The OLD path still works as fallback for programmatic matrix changes outside of layout.

**Option C — Immediate matrix application**: Instead of `MarkLocalModified(deferred: true)`, use immediate matrix recalculation during the arrange phase. This eliminates the one-frame stale position issue but requires care to avoid re-entrant layout.

---

## Issue 2: `XRBase.SetField` Uses `ReferenceEquals` for Value Types (PERFORMANCE)

### Description

`XRBase.SetField<T>(ref T field, T value)` uses `ReferenceEquals(field, value)` to check equality before setting the field and firing `PropertyChanged`.

For **value types** (e.g., `Vector2`, `float`, `int`), this always returns `false` because each boxing operation creates a new object — so `PropertyChanged` fires even when the value hasn't changed.

### Impact

Without mitigation, every frame:
- `OnResizeActual` sets `ActualSize` and `ActualLocalBottomLeftTranslation` via property setters
- `PropertyChanged` fires on `UICanvasTransform` for `ActualSize`
- `OnCanvasTransformPropertyChanged` calls `ResizeScreenSpace()`
- `ResizeScreenSpace` remakes the entire quadtree and reinitializes the ortho camera
- **Result**: Quadtree remade every frame → massive performance hit

### Current Mitigation

Added in `UIBoundableTransform.OnResizeActual`:
```csharp
bool sizeChanged = !XRMath.VectorsEqual(_actualSize, size);
bool posChanged = !XRMath.VectorsEqual(_actualLocalBottomLeftTranslation, bottomLeftTranslation);
if (sizeChanged) ActualSize = size;
if (posChanged) ActualLocalBottomLeftTranslation = bottomLeftTranslation;
```

### Remaining Risk

Any other value-type property with a `SetField` setter will fire PropertyChanged every time it's called, even with the same value. This affects:
- `AxisAlignedRegion` (`BoundingRectangleF`)
- `RegionWorldTransform` (`Matrix4x4`)
- `Margins`, `Padding` (`Vector4`)
- Any `float` or `bool` field using `SetField`

### Proper Fix

Fix `XRBase.SetField` to use `EqualityComparer<T>.Default.Equals(field, value)` instead of `ReferenceEquals`:
```csharp
protected bool SetField<T>(ref T field, T value, ...)
{
    if (EqualityComparer<T>.Default.Equals(field, value))
        return false;
    // ... fire PropertyChanged
}
```

This works correctly for both value types and reference types.

---

## Issue 3: `UIGridTransform` and `UITabTransform` Missing `ArrangeChildren` Override

### Description

These transforms ONLY override `OnResizeChildComponents` (OLD path):

| Transform | Has `ArrangeChildren`? | Has `OnResizeChildComponents`? |
|-----------|----------------------|-------------------------------|
| `UIGridTransform` | **NO** | YES (complex grid layout) |
| `UITabTransform` | **NO** | YES (show/hide by index) |
| `UIDualSplitTransform` | YES | YES |
| `UIMultiSplitTransform` | YES | YES |
| `UIDockingRootTransform` | YES | YES |
| `UIListTransform` | YES | YES |

### Impact

When the NEW layout path runs for these transforms, they fall through to the default `UILayoutSystem.ArrangeChildrenBoundable` which passes the **same padded region to ALL children** — stacking them on top of each other instead of arranging them in a grid or showing only the selected tab.

The OLD path (triggered later via `OnLocalMatrixChanged`) then runs the correct layout, but with the timing issues described in Issue 1.

### Fix

Add `ArrangeChildren` overrides to both transforms. Reference the existing `OnResizeChildComponents` logic:

**UIGridTransform**: Port the grid calculation logic (sizing modes, proportional distribution, cell positioning) into an `ArrangeChildren` override that uses `UILayoutSystem.ArrangeBoundable` or `bc.Arrange()` instead of `bc.FitLayout()`.

**UITabTransform**: Override `ArrangeChildren` to show/collapse children based on `SelectedIndex`.

---

## Issue 4: OLD Path Uses `ActualWidth/Height` vs NEW Path Uses `DesiredSize`

### Description

`UIListTransform` has inconsistent sizing between paths:

**NEW path** (`ArrangeChildren`):
```csharp
float size = ItemSize ?? bc.DesiredSize.Y;
```

**OLD path** (`SizeChildrenLeftTop`):
```csharp
float size = ItemSize ?? bc.ActualHeight;
```

### Impact

`DesiredSize` comes from the measure phase and includes margins. `ActualHeight` comes from `OnResizeActual` and is the post-layout size. If children haven't been laid out yet, `ActualHeight` may be 0 or stale.

This means the same list can produce different layouts depending on which path runs, and in which order.

### Fix

Standardize on `DesiredSize` in the NEW path (which is correct — measure happens before arrange). Ensure the OLD path either uses `DesiredSize` too, or is removed entirely per Issue 1.

---

## Issue 5: PlacementInfo Offset + Layout Position = Double Positioning

### Description

When `ArrangeChildren` in split/dock transforms calls `UILayoutSystem.FitLayout(child, subRegion)`, the subRegion already specifies the position (e.g., `x = leftSize + splitterSize`). But the parent also sets `PlacementInfo.Offset` to the same value.

In `CreateLocalMatrix`:
```csharp
mtx = Translation(ActualLocalBottomLeftTranslation, depth);
mtx *= PlacementInfo.GetRelativeItemMatrix();  // Adds offset AGAIN
```

If `ActualLocalBottomLeftTranslation` was computed from `GetActualBounds` which used `parentBounds` that already has the offset baked into position, the offset is applied twice.

### Analysis

This depends on anchor configuration:
- **Point anchor with Translation=(0,0)**: `GetActualBounds` computes position relative to parent origin → PlacementInfo adds the split offset → correct single positioning
- **Stretched anchor (0,0)→(1,1)**: `GetActualBounds` computes position = 0 relative to sub-region → PlacementInfo adds offset → likely correct if sub-region origin was at parent origin

The current behavior likely works because `GetActualBounds` receives the sub-region as parentBounds, and with default anchors (0,0)→(1,1) the bottomLeftTranslation = 0 relative to the sub-region. Then `PlacementInfo.Offset` translates from the parent's origin to the sub-region's origin.

However, if a child has **non-default anchors** inside a split panel, the interaction between the anchor-based position calculation and the PlacementInfo offset may produce incorrect results.

### Recommendation

Audit all split/dock/list transforms to verify that PlacementInfo offsets and `GetActualBounds` parentBounds are consistent. Consider whether PlacementInfo should be eliminated in favor of baking the offset into the parentBounds passed to `ArrangeBoundable`.

---

## Issue 6: `UITransform.FitLayout` is Empty

### Description

`UITransform.FitLayout(BoundingRectangleF)` is an empty virtual method:
```csharp
public virtual void FitLayout(BoundingRectangleF parentRegion)
{
    // empty
}
```

Only `UIBoundableTransform.FitLayout` has an implementation:
```csharp
public override void FitLayout(BoundingRectangleF parentBounds)
{
    UILayoutSystem.FitLayout(this, parentBounds);
}
```

### Impact

If a child is a plain `UITransform` (e.g., `UIRotationTransform`) and the parent's `OnResizeChildComponents` calls `child.FitLayout(region)`, that call does **nothing**. The rotation transform and its descendants get zero layout.

### Fix

Either:
1. Make all UI transforms extend `UIBoundableTransform` (breaking change)
2. Implement `UITransform.FitLayout` to at least propagate to children:
   ```csharp
   public virtual void FitLayout(BoundingRectangleF parentRegion)
   {
       OnResizeActual(parentRegion);
       OnResizeChildComponents(parentRegion);
   }
   ```

---

## Issue 7: Auto-Sizing Circular Dependencies

### Description

When a parent has `Width = null` (auto-size), it measures by calling `GetMaxChildWidth()` which queries children. If a child has stretched anchors (0,0)→(1,1), its size depends on the parent size. This creates a circular dependency.

### Current Behavior

The measure phase calculates desired size bottom-up, but stretched-anchor children don't contribute meaningful sizes during measure because their size is determined during arrange (top-down). This means auto-sized parents with stretched children may get size 0.

### Workaround

Auto-sized parents should have children with explicit sizes or point anchors. The system works correctly when:
- Parent auto-sizes + children have explicit Width/Height
- Parent has explicit size + children stretch (anchor 0→1)

**Mixing auto-sizing parents with stretched children is unsupported.**

---

## Issue 8: Thread Safety Concerns

### Description

Several collections are iterated without synchronization, with commented-out `lock` statements:

```csharp
// Seen throughout the codebase:
//lock (Children)
//{
    foreach (var c in Children)
        ...
//}
```

### Impact

If children are added/removed on a different thread while layout is running, `InvalidOperationException` (collection modified during enumeration) or worse can occur.

### Mitigation

Layout runs on the main thread during `CollectVisible`. As long as UI hierarchy modifications also happen on the main thread, this is safe. If async layout (`UseAsyncLayout`) is used, this becomes a real concern.

---

## Summary: Priority Matrix

| Issue | Severity | Impact | Fix Difficulty |
|-------|----------|--------|---------------|
| 1. Dual layout path | **Critical** | Wrong positions, double work | Medium-Hard |
| 2. SetField ReferenceEquals | **High** | Perf (mitigated) | Easy (root fix) |
| 3. Grid/Tab missing ArrangeChildren | **High** | Grid layout broken in NEW path | Easy |
| 4. ActualSize vs DesiredSize | **Medium** | Inconsistent list sizing | Easy |
| 5. PlacementInfo double offset | **Medium** | May cause offsets with non-default anchors | Medium |
| 6. Empty UITransform.FitLayout | **Medium** | Rotation/non-bounded children get no layout | Easy |
| 7. Auto-size circular deps | **Low** | Only affects uncommon configurations | Design issue |
| 8. Thread safety | **Low** | Only affects async layout | Medium |
