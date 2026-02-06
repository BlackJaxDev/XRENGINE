# 01 — UI Layout System Architecture

## Overview

The XREngine UI layout system uses a **two-phase measure/arrange model** inspired by WPF/UWP, centralized in the static class `UILayoutSystem`. All layout logic flows through this single class, while individual transform types customize behavior via virtual method overrides.

**Coordinate system**: Origin at bottom-left, +X right, +Y up. All positions/sizes are in pixels for screen-space canvases.

---

## 1. Layout Phases

### Phase 1: Measure (Bottom-Up)

**Purpose**: Calculate each element's `DesiredSize` — how much space it *wants*.

**Entry point**: `UILayoutSystem.MeasureTransform(canvas, availableSize)`

**Flow**:
```
MeasureTransform(transform, availableSize)
  ├── if UIBoundableTransform → MeasureBoundable()
  │     ├── Skip if !NeedsMeasure and same constraints (version-based dirty check)
  │     ├── Width.HasValue?  → use explicit Width
  │     │   else CalcAutoWidthCallback?  → use callback
  │     │   else InvokeMeasureChildrenWidth() → virtual, overridable
  │     ├── Height.HasValue?  → use explicit Height  
  │     │   else CalcAutoHeightCallback?  → use callback
  │     │   else InvokeMeasureChildrenHeight() → virtual, overridable
  │     ├── ClampSize(min/max constraints)
  │     ├── Add margins to desired size
  │     └── Store in DesiredSize, mark as measured
  └── else (plain UITransform)
        ├── MeasureChildren() → max of all child desired sizes
        └── Store in DesiredSize
```

**Key details**:
- Dirty checking uses `_layoutVersion` vs `_lastMeasuredVersion` — if equal, skip measure
- Also checks `_lastMeasureConstraint` — re-measure if available size changed
- Auto-sizing: When `Width` or `Height` is `null`, size comes from callbacks or child sizes
- `MeasureChildrenWidth/Height` are virtual on `UIBoundableTransform` — `UIListTransform` overrides them to sum child sizes instead of taking the max

### Phase 2: Arrange (Top-Down)

**Purpose**: Assign each element's final position and size based on parent bounds.

**Entry point**: `UILayoutSystem.ArrangeTransform(canvas, canvasBounds)`

**Flow**:
```
ArrangeTransform(transform, finalBounds)
  ├── if UIBoundableTransform → ArrangeBoundable()
  │     ├── Skip if !NeedsArrange and same bounds
  │     ├── OnResizeActualInternal(finalBounds)
  │     │     └── GetActualBounds() → compute position & size from anchors, margins, named size
  │     │     └── Set ActualSize, ActualLocalBottomLeftTranslation (with change guards)
  │     │     └── MarkLocalModified(deferred: true) if changed
  │     ├── ShouldMarkLocalMatrixChanged() → MarkLocalModified() if matrix inputs changed
  │     ├── Mark as arranged
  │     ├── GetActualBounds() → BoundingRectangleF for children
  │     └── InvokeArrangeChildren(actualBounds) → calls virtual ArrangeChildren()
  └── else (plain UITransform)
        ├── OnResizeActualInternal() → sets _actualLocalBottomLeftTranslation = Translation
        └── ArrangeChildren() → iterate children, call ArrangeTransform on each
```

**Key details**:
- `ArrangeChildren` is the main extension point — split/list/grid transforms override this
- Default `ArrangeChildren` in `UIBoundableTransform` delegates to `UILayoutSystem.ArrangeChildrenBoundable()` which applies padding and passes the same padded region to ALL children
- Custom transforms (list, split, grid) compute per-child regions in their override

---

## 2. Canvas Entry Point

Layout is triggered from `UICanvasComponent.CollectVisibleItemsScreenSpace()`:

```
CollectVisibleItemsScreenSpace(scene, items)
  └── CanvasTransform.UpdateLayout()
        └── UILayoutSystem.UpdateCanvasLayout(canvas)
              ├── Guard: skip if !IsLayoutInvalidated or IsNestedCanvas
              ├── CancelLayoutJob() (cancel any async job)
              ├── bounds = GetRootCanvasBounds()  // (0,0, Width, Height)
              ├── Phase 1: MeasureTransform(canvas, bounds.Extents)
              ├── Phase 2: ArrangeTransform(canvas, bounds)
              └── Raise LayoutingFinished event
```

**When does this run?** Every frame during render collection if `IsLayoutInvalidated` is true.

**Canvas bounds**: For screen-space canvases, `GetRootCanvasBounds()` returns `(0, 0, GetWidth(), GetHeight())`. The canvas Width/Height are set via `UICanvasComponent.ResizeScreenSpace()` which is called when the viewport resizes.

---

## 3. Invalidation & Dirty Tracking

### Version-Based System

Each `UITransform` has:
- `_layoutVersion` (volatile uint) — incremented when layout-affecting properties change
- `_lastMeasuredVersion` — snapshot of `_layoutVersion` after last measure
- `_lastArrangedVersion` — snapshot of `_layoutVersion` after last arrange

```
NeedsMeasure  = _lastMeasuredVersion != _layoutVersion
NeedsArrange  = _lastArrangedVersion != _layoutVersion
```

### What Invalidates Layout

| Property Change | Calls | Result |
|----------------|-------|--------|
| `Translation`, `Scale`, `DepthTranslation` | `InvalidateLayout()` | Full re-layout |
| `Visibility` | `InvalidateLayout()` | Full re-layout |
| `Parent`, `ParentCanvas` | `InvalidateLayout()` | Full re-layout |
| `PlacementInfo` | `InvalidateLayout()` | Full re-layout |
| `Width`, `Height`, `Min/MaxWidth/Height` | `InvalidateMeasure()` | Re-measure + re-arrange |
| `Margins`, `Padding`, `NormalizedPivot` | `InvalidateMeasure()` | Re-measure + re-arrange |
| `MinAnchor`, `MaxAnchor` | `InvalidateArrange()` | Re-arrange only |

### Propagation

`InvalidateLayout()`:
1. Increments `_layoutVersion` on the transform
2. Calls `ParentCanvas.InvalidateLayout()` → sets `IsLayoutInvalidated = true` on the root canvas
3. Calls `MarkLocalModified(true)` to trigger deferred matrix recalculation

`InvalidateMeasure()`:
1. Increments `_layoutVersion`
2. Propagates upward if parent `UsesAutoSizing` (parent size depends on children)
3. Marks canvas as invalidated

`InvalidateArrange()`:
1. Increments `_layoutVersion` (only if `NeedsMeasure` is false)
2. Marks canvas as invalidated

### Canvas Invalidation

When canvas size changes (e.g., window resize):
```
UICanvasComponent.OnCanvasTransformPropertyChanged("ActualSize")
  → ResizeScreenSpace() → remakes quadtree, ortho camera
  → InvalidateCanvasLayout(canvas) 
      → IncrementLayoutVersion()
      → SetLayoutInvalidated(true)
      → InvalidateChildrenRecursive() → ForceInvalidateArrange() on ALL descendants
```

---

## 4. Bounds Calculation — `GetActualBounds`

The most complex part of layout. Determines final position and size from:
- Parent bounds (passed as `BoundingRectangleF parentBounds`)
- Anchors (`MinAnchor`, `MaxAnchor`)
- Translation
- Width/Height (explicit or auto)
- Margins

### Anchor Modes

**Point anchor** (`MinAnchor == MaxAnchor` on an axis):
- Size comes from `GetWidth()` / `GetHeight()` (explicit or auto)
- Position = anchor position + Translation - (pivot * size)
- Margins applied as positional offsets (lerped between left/right based on anchor X)

**Stretched anchor** (`MinAnchor != MaxAnchor` on an axis):
- Size = (maxAnchorPos + Width) - (minAnchorPos + Translation) - leftMargin - rightMargin
- Position = minAnchorPos - (pivot * size) + leftMargin
- Translation acts as inset from min anchor, Width acts as inset from max anchor

### Pseudocode
```
GetActualBounds(transform, parentBounds):
  minX = parentWidth * MinAnchor.X
  maxX = parentWidth * MaxAnchor.X
  
  if MinAnchor.X ≈ MaxAnchor.X:  // Point anchor
    size.X = GetWidth()
  else:  // Stretched
    size.X = (maxX + Width) - (minX + Translation.X) - Margins.Left - Margins.Right
  
  // Same for Y axis...
  
  ClampSize(min/max constraints)
  
  // Pivot adjustment
  minX -= NormalizedPivot.X * size.X
  minY -= NormalizedPivot.Y * size.Y
  
  // Translation for point anchors
  if point_X: minX += Translation.X
  if point_Y: minY += Translation.Y
  
  // Margin application
  if point_X: marginX = lerp(Margins.Left, -Margins.Right, MinAnchor.X)
  else:       marginX = Margins.Left
  
  bottomLeftTranslation = (minX + marginX, minY + marginY)
```

---

## 5. The Deferred Matrix / Local Matrix Pipeline

When `OnResizeActual` sets `ActualLocalBottomLeftTranslation`, it calls `MarkLocalModified(deferred: true)`. This:
1. Marks the transform as needing local matrix recalculation
2. On next `SwapBuffers` / frame sync, `RecalculateMatrices()` runs
3. Calls `CreateLocalMatrix()` → builds matrix from `ActualLocalBottomLeftTranslation`, `PlacementInfo`, pivot, scale, rotation
4. Calls `OnLocalMatrixChanged()` → **triggers the OLD layout path**

### `CreateLocalMatrix` for UIBoundableTransform
```csharp
Matrix4x4 mtx = CreateTranslation(ActualLocalBottomLeftTranslation, DepthTranslation);
if (PlacementInfo != null)
    mtx *= PlacementInfo.GetRelativeItemMatrix();  // Split offset, dock offset, etc.
if (Scale != 1 or Rotation != 0)
    mtx *= pivot offset → scale → rotation → un-pivot;
```

### PlacementInfo

Each parent transform type has a custom `UIChildPlacementInfo` subclass:
- `UISplitChildPlacementInfo` → `Offset` property, translates along split axis
- `UIDockingPlacementInfo` → `BottomLeft` property, translates to dock region
- `UIListChildPlacementInfo` → `BottomOrLeftOffset`, translates along list axis
- `UIGridChildPlacementInfo` → Row/Column/Span properties (used for grid placement)

`PlacementInfo.GetRelativeItemMatrix()` returns a translation matrix that positions the child within the parent's space.

---

## 6. The OLD Layout Path (`OnResizeChildComponents`)

After `CreateLocalMatrix` runs, `OnLocalMatrixChanged` fires on `UIBoundableTransform`:

```csharp
// UIBoundableTransform.OnLocalMatrixChanged:
OnResizeChildComponents(ApplyPadding(GetActualBounds()));
```

This calls the virtual `OnResizeChildComponents` which:
- **Default** (`UITransform`): Iterates children, calls `child.FitLayout(parentRegion)`
- **Default** (`UIBoundableTransform`): Inherits from UITransform
- **Custom overrides**: Split/dock/list/grid transforms compute sub-regions

`FitLayout` on `UIBoundableTransform` calls:
```csharp
UILayoutSystem.MeasureBoundable(this, parentBounds.Extents);
UILayoutSystem.ArrangeBoundable(this, parentBounds);
```

So the OLD path re-runs the NEW path's logic on children — effectively doing a **second layout pass**.

---

## 7. Async Layout

The layout system supports coroutine-based async layout to avoid frame hitches:

```csharp
canvas.UseAsyncLayout = true;
canvas.MaxLayoutItemsPerFrame = 50;
canvas.UpdateLayoutAsync(); // Schedules a Job
```

`UILayoutSystem.LayoutCoroutine()`:
1. Phase 1: MeasureCoroutine — measures transforms recursively, yielding every N items
2. Phase 2: ArrangeCoroutine — arranges transforms recursively, yielding every N items

This is scheduled as a `Job` on `Engine.Jobs`. Layout completion fires `LayoutingFinished`.

---

## 8. Summary of Extension Points

| Virtual Method | Class | Purpose | Who Uses It |
|---|---|---|---|
| `ArrangeChildren(childRegion)` | `UIBoundableTransform` | Custom child arrangement (NEW path) | `UIListTransform`, `UIDualSplitTransform`, `UIMultiSplitTransform`, `UIDockingRootTransform` |
| `OnResizeChildComponents(parentRegion)` | `UITransform` | Custom child arrangement (OLD path) | Same as above plus `UIGridTransform`, `UITabTransform`, `UIScrollableTransform` |
| `MeasureChildrenWidth(availableSize)` | `UIBoundableTransform` | Custom width measurement | `UIListTransform` |
| `MeasureChildrenHeight(availableSize)` | `UIBoundableTransform` | Custom height measurement | `UIListTransform` |
| `GetActualBounds(parentBounds, ...)` | `UIBoundableTransform` | Custom bounds calculation | `UIFittedTransform` |
| `GetMaxChildWidth()` | `UITransform` | Auto-size width from children | `UIListTransform` (sums instead of max) |
| `GetMaxChildHeight()` | `UITransform` | Auto-size height from children | `UIListTransform` (sums instead of max) |
| `VerifyPlacementInfo(child, ref info)` | `UITransform` | Create child-specific placement info | Splits, dock, list, grid |
