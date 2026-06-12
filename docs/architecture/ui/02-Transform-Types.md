# 02 — UI Transform Types Reference

Every UI element's position and size is determined by its transform. This document describes each transform type, its properties, and how it participates in layout.

---

## Inheritance Hierarchy

```
TransformBase
└── UITransform                     (base: 2D translation, scale, rotation)
    └── UIBoundableTransform        (adds: size, anchors, margins, padding)
        ├── UICanvasTransform       (root: screen/camera/world space)
        ├── UIFittedTransform       (stretch/center/fill fit modes)
        ├── UIScrollableTransform   (scrollable container)
        ├── UIDualSplitTransform    (two-panel split)
        ├── UIMultiSplitTransform   (multi-panel split)
        ├── UIDockingRootTransform  (center/left/right/bottom dock)
        ├── UIListTransform         (vertical/horizontal list)
        ├── UIGridTransform         (row/column grid)
        ├── UITabTransform          (tabbed container, one child visible)
        └── UIDockableTransform     (drag-drop docking support)
    └── UIRotationTransform         (rotation-only wrapper, NOT boundable)
```

---

## UITransform

**File**: `Transforms/UITransform.cs` (589 lines)

The non-bounded base class. Has position but no inherent size.

### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Translation` | `Vector2` | `(0,0)` | 2D position offset |
| `ActualLocalBottomLeftTranslation` | `Vector2` | `(0,0)` | Final computed position after layout |
| `DepthTranslation` | `float` | `0` | Z-offset for depth sorting |
| `Scale` | `Vector3` | `(1,1,1)` | Scale factor |
| `RotationRadians` | `float` | `0` | Z-axis rotation |
| `Visibility` | `EVisibility` | `Visible` | Visible/Hidden/Collapsed |
| `ParentCanvas` | `UICanvasTransform?` | `null` | Root canvas (auto-set from parent chain) |
| `PlacementInfo` | `UIChildPlacementInfo?` | `null` | Parent-supplied positioning (lazy-created) |

### Layout State

| Field | Type | Description |
|-------|------|-------------|
| `_layoutVersion` | `uint` (volatile) | Increments on any layout-affecting change |
| `_lastMeasuredVersion` | `uint` | Snapshot after last measure |
| `_lastArrangedVersion` | `uint` | Snapshot after last arrange |
| `_desiredSize` | `Vector2` | Result of measure phase |
| `_lastMeasureConstraint` | `Vector2` | Available size from last measure |
| `_lastArrangeBounds` | `BoundingRectangleF` | Bounds from last arrange |

### Layout Behavior

- **Measure**: Returns max of children's desired sizes
- **Arrange**: Sets `_actualLocalBottomLeftTranslation = Translation`, then arranges children in same region
- **FitLayout** (instance): **EMPTY** — does nothing! Only `UIBoundableTransform.FitLayout` calls into the layout system
- **OnResizeChildComponents**: Iterates children, calls `child.FitLayout(parentRegion)` on each (OLD path)
- **CreateLocalMatrix**: `Scale * RotationZ * Translation3D`

### Invalidation Triggers

`Translation`, `DepthTranslation`, `Scale`, `Visibility`, `ParentCanvas`, `Parent`, `PlacementInfo` → `InvalidateLayout()`

---

## UIBoundableTransform

**File**: `Transforms/UIBoundableTransform.cs` (765 lines)

The main base class for all sized UI elements. Adds size, anchors, margins, and padding.

### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ActualSize` | `Vector2` | `(0,0)` | Computed size after layout |
| `Width` | `float?` | `null` | Explicit width (`null` = auto-size) |
| `Height` | `float?` | `null` | Explicit height (`null` = auto-size) |
| `MinWidth` | `float?` | `null` | Minimum width constraint |
| `MaxWidth` | `float?` | `null` | Maximum width constraint |
| `MinHeight` | `float?` | `null` | Minimum height constraint |
| `MaxHeight` | `float?` | `null` | Maximum height constraint |
| `MinAnchor` | `Vector2` | `(0,0)` | Anchor point (normalized to parent) |
| `MaxAnchor` | `Vector2` | `(1,1)` | Anchor point (normalized to parent) |
| `NormalizedPivot` | `Vector2` | `(0,0)` | Pivot for scale/rotation (normalized to own size) |
| `Margins` | `Vector4` | `(0,0,0,0)` | Outside margins: X=left, Y=bottom, Z=right, W=top |
| `Padding` | `Vector4` | `(0,0,0,0)` | Inside padding: X=left, Y=bottom, Z=right, W=top |
| `CalcAutoWidthCallback` | `Func<..., float>?` | `null` | Custom auto-width calculator |
| `CalcAutoHeightCallback` | `Func<..., float>?` | `null` | Custom auto-height calculator |
| `ExcludeFromParentAutoCalcWidth` | `bool` | `false` | Skip this child when parent auto-sizes width |
| `ExcludeFromParentAutoCalcHeight` | `bool` | `false` | Skip this child when parent auto-sizes height |

### Anchor Behavior

When `MinAnchor == MaxAnchor` (point anchor):
- Size = `Width ?? CalcAutoCallback ?? GetMaxChildWidth/Height()`
- Position = anchor * parent size + Translation - pivot * size + margin offset

When `MinAnchor != MaxAnchor` (stretched anchor):
- Size = (maxAnchor * parent - rightMargin) - (minAnchor * parent + Translation + leftMargin)
- `Width`/`Height` act as insets from the max anchor edge
- Position = minAnchor * parent - pivot * size + leftMargin

### Layout Behavior

- **Measure**: Uses `UILayoutSystem.MeasureBoundable` — explicit size or auto from children
- **Arrange**: Uses `UILayoutSystem.ArrangeBoundable` → `OnResizeActual` → `ArrangeChildren`
- **ArrangeChildren** (virtual): Default calls `UILayoutSystem.ArrangeChildrenBoundable` — applies padding, passes same region to all children
- **FitLayout** (instance): `UILayoutSystem.FitLayout(this, parentBounds)` → Measure + Arrange
- **OnLocalMatrixChanged**: `OnResizeChildComponents(ApplyPadding(GetActualBounds()))` — triggers OLD path
- **OnResizeChildComponents**: Inherited from `UITransform` — iterates children calling `FitLayout`

### CreateLocalMatrix
```
Translation(ActualLocalBottomLeftTranslation, DepthTranslation)
  × PlacementInfo.GetRelativeItemMatrix()    (if exists)
  × Pivot → Scale → Rotation → Un-Pivot     (if non-identity)
```

### Invalidation Triggers

| Properties | Method |
|-----------|--------|
| `Width`, `Height`, `Min/MaxWidth/Height`, `Margins`, `Padding`, `NormalizedPivot` | `InvalidateMeasure()` |
| `MinAnchor`, `MaxAnchor` | `InvalidateArrange()` |
| (Inherited from UITransform) | `InvalidateLayout()` |

---

## UICanvasTransform

**File**: `Transforms/UICanvasTransform.cs` (287 lines)

Root of a UI hierarchy. Defines draw space and manages layout scheduling.

### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `DrawSpace` | `ECanvasDrawSpace` | `Screen` | Screen / Camera / World |
| `CameraSpaceCamera` | `XRCamera?` | `null` | Camera for camera-space rendering |
| `CameraDrawSpaceDistance` | `float` | `1.0` | Distance from camera |
| `IsLayoutInvalidated` | `bool` | `true` | Thread-safe flag: layout needs recalc |
| `IsUpdatingLayout` | `bool` | `false` | Thread-safe flag: layout in progress |
| `UseAsyncLayout` | `bool` | `false` | Use coroutine-based incremental layout |
| `MaxLayoutItemsPerFrame` | `int` | `50` | Items per frame in async mode |

### Layout Behavior

- **UpdateLayout()**: Calls `UILayoutSystem.UpdateCanvasLayout(this)` — synchronous full layout
- **UpdateLayoutAsync()**: Schedules `LayoutCoroutine` as a Job
- **InvalidateLayout()**: Base invalidation + `UILayoutSystem.InvalidateCanvasLayout(this)` which also recursively force-invalidates all children
- **GetRootCanvasBounds()**: Returns `(0, 0, GetWidth(), GetHeight())`
- **IsNestedCanvas**: True if `ParentCanvas != null && ParentCanvas != this` — skips root layout for nested canvases

### CreateWorldMatrix
- **Screen space**: `Matrix4x4.Identity`
- **Camera space**: Positioned at camera's bottom-left at specified depth, oriented with camera
- **World space**: Uses standard parent-chain world matrix

### Size Management

`SetSize(Vector2)` sets Width, Height, and anchors to (0,0) with pivot at (0,0). This is called by `UICanvasComponent.ResizeScreenSpace()` when the viewport dimensions change.

---

## UIDualSplitTransform

**File**: `Transforms/UIDualSplitTransform.cs` (283 lines)

Splits available space between two children along a horizontal or vertical axis.

### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `SplitDirection` | `ESplitDirection` | `Horizontal` | Split axis |
| `SplitPercent` | `float` | `0.5` | Where to split (0–1) |
| `FixedSize` | `float?` | `null` | Fixed size for first panel (overrides percent) |
| `SplitterSize` | `float` | `0.0` | Gap between panels |
| `CanUserResize` | `bool` | `true` | Allow interactive resize |

### PlacementInfo

`UISplitChildPlacementInfo` — `Offset` property used in `GetRelativeItemMatrix()` to translate child along split axis.

### Layout Behavior

**ArrangeChildren** (NEW path): Applies padding, computes split sizes, calls `UILayoutSystem.FitLayout()` on each child with their sub-region.

**OnResizeChildComponents** (OLD path): Same logic using `child.FitLayout()` instance method.

---

## UIMultiSplitTransform

**File**: `Transforms/UIMultiSplitTransform.cs` (503 lines)

Splits space into 2 or 3 regions with multiple arrangement patterns.

### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Arrangement` | `UISplitArrangement` | `LeftMiddleRight` | Layout pattern |
| `FixedSizeFirst` | `float?` | `null` | Fixed size for first region |
| `FixedSizeSecond` | `float?` | `null` | Fixed size for last region |
| `SplitPercentFirst` | `float` | `0.33` | Split point for first boundary |
| `SplitPercentSecond` | `float` | `0.66` | Split point for second boundary |
| `SplitterSize` | `float` | `0.0` | Gap between regions |

### Arrangements

| Value | Regions | Axis |
|-------|---------|------|
| `LeftMiddleRight` | 3 horizontal | X |
| `TopMiddleBottom` | 3 vertical | Y |
| `LeftRight` | 2 horizontal | X |
| `TopBottom` | 2 vertical | Y |

### Size Calculation

Fixed sizes take priority. Remaining space is distributed by split percentages. If child count < expected, falls back to simpler arrangements.

### PlacementInfo

`UISplitChildPlacementInfo` — same as DualSplit. `Offset` translates along the split direction. `Vertical` property checks arrangement type.

### Layout Behavior

Both `ArrangeChildren` (NEW) and `OnResizeChildComponents` (OLD) implement the same region calculation logic. Each calls `FitLayout` on children with computed sub-regions.

---

## UIDockingRootTransform

**File**: `Transforms/UIDockingRootTransform.cs` (+ 158 lines total with related classes)

Arranges exactly 4 children: center, left, right, bottom.

### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `LeftSizeWidth` | `float` | `300.0` | Width of left panel |
| `RightSizeWidth` | `float` | `300.0` | Width of right panel |
| `BottomSizeHeight` | `float` | `200.0` | Height of bottom panel |

### Child Accessors

| Accessor | Index | Region |
|----------|-------|--------|
| `Center` | 0 | Center (remaining space) |
| `Left` | 1 | Left strip |
| `Right` | 2 | Right strip |
| `Bottom` | 3 | Bottom strip |

### Region Calculation

```
Left:   (0, BottomSizeHeight, LeftSizeWidth, parentHeight)
Right:  (parentWidth - RightSizeWidth, 0, RightSizeWidth, parentHeight)
Bottom: (0, parentHeight - BottomSizeHeight, parentWidth, BottomSizeHeight)
Center: (LeftSizeWidth, BottomSizeHeight, remaining width, parentHeight)
```

### PlacementInfo

`UIDockingPlacementInfo` — `BottomLeft` property; `GetRelativeItemMatrix()` translates by BottomLeft.

### Auto-Creation

Both paths auto-create children up to 4 if SceneNode exists.

---

## UIListTransform

**File**: `Arrangements/UIListTransform.cs` (617 lines)

Arranges children sequentially — vertical or horizontal.

### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ItemSize` | `float?` | `null` | Uniform size per item (`null` = use child's own size) |
| `DisplayHorizontal` | `bool` | `false` | Horizontal (true) or vertical (false) list |
| `ItemSpacing` | `float` | `0.0` | Gap between items |
| `ItemAlignment` | `EListAlignment` | `TopOrLeft` | TopOrLeft / Centered / BottomOrRight |
| `Virtual` | `bool` | `false` | Cull items outside virtual bounds |
| `UpperVirtualBound` | `float` | `0.0` | Upper visibility bound |
| `LowerVirtualBound` | `float` | `0.0` | Lower visibility bound |

### Custom Measure

Overrides `MeasureChildrenWidth` and `MeasureChildrenHeight`:
- For the list axis: **sums** child sizes + spacing (not max)
- For the cross axis: uses base (max of children)
- Each child is measured first if `NeedsMeasure`
- Uses `ItemSize` if set, otherwise `child.DesiredSize`

### Custom Arrange

Overrides `ArrangeChildren` with three alignment strategies:

**TopOrLeft**: Starts from top (vertical) or left (horizontal), decrements Y or increments X.

**Centered**: Calculates total size, centers offset, then lays out sequentially.

**BottomOrRight**: Starts from bottom/right, moves opposite direction.

Each child gets `bc.Arrange(new BoundingRectangleF(...))` with computed position and size.

### Virtualization

When `Virtual = true`, items outside `LowerVirtualBound`–`UpperVirtualBound` get `Visibility = Hidden` and their scene node deactivated. Scrolling updates bounds.

### PlacementInfo

`UIListChildPlacementInfo` — `BottomOrLeftOffset` property; `GetRelativeItemMatrix()` translates along list axis.

### Old Path

`OnResizeChildComponents` — Three methods `SizeChildrenLeftTop/Centered/RightBottom` with same logic using `FitLayout` instead of `Arrange`.

**Important difference**: OLD path uses `ItemSize ?? bc.ActualWidth/Height` while NEW path uses `ItemSize ?? bc.DesiredSize.X/Y`.

---

## UIGridTransform

**File**: `Arrangements/UIGridTransform.cs` (515 lines)

Arranges children in a grid defined by row and column sizing definitions.

### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Rows` | `EventList<UISizingDefinition>` | `[]` | Row definitions |
| `Columns` | `EventList<UISizingDefinition>` | `[]` | Column definitions |
| `InvertY` | `bool` | `false` | Invert vertical ordering |

### Sizing Modes (UISizingDefinition)

| Mode | Behavior |
|------|----------|
| `Fixed` | Exact pixel size |
| `Auto` | Size to content (max child size in that row/col) |
| `Proportional` | Share remaining space proportionally |

### Layout Algorithm (OnResizeChildComponents only — no ArrangeChildren override!)

1. **Pre-pass**: Set fixed sizes, collect auto-sized components, accumulate proportional denominators
2. **Auto pass**: Calculate auto row heights and column widths from child content
3. **Remaining space**: Subtract fixed + auto from parent, distribute to proportional
4. **Arrange pass**: Iterate rows × columns, compute (x, y, width, height) for each cell, call `child.FitLayout()`

### PlacementInfo

`UIGridChildPlacementInfo` — `Row`, `Column`, `RowSpan`, `ColumnSpan` properties. Grid uses an indices matrix `List<int>[rows, cols]` to map children to cells.

### ⚠️ Note

**UIGridTransform only overrides `OnResizeChildComponents` (OLD path)**. It does NOT override `ArrangeChildren`, so the NEW layout path uses the default `ArrangeChildrenBoundable` which stacks all children on top of each other.

---

## UITabTransform

**File**: `UIDockableTransform1.cs` (lines 1–77)

Shows one child at a time; other children are collapsed.

### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `SelectedIndex` | `int` | `0` | Currently visible child |
| `TabHeight` | `float` | `30.0` | Reserved height for tab bar |

### Layout Behavior

Only overrides `OnResizeChildComponents` (OLD path):
- Selected child: `FitLayout(parentRegion)` + `Visibility = Visible`
- Other children: `Visibility = Collapsed`

**⚠️ Does NOT override `ArrangeChildren` (NEW path).**

---

## UIFittedTransform

**File**: `Transforms/UIFittedTransform.cs` (73 lines)

Custom fit behavior within parent bounds.

### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `FitType` | `EFitType` | `None` | Fit mode |

### Fit Modes

| Mode | Behavior |
|------|----------|
| `None` | Standard bounds calculation |
| `Stretch` | Size = parent extents (fill completely) |
| `Center` | Maintain aspect ratio, fit inside parent (letterbox) |
| `Fill` | Maintain aspect ratio, fill parent (crop) |

### Extension Point

Overrides `GetActualBounds` — after base calculation, modifies size based on fit type.

---

## UIScrollableTransform

**File**: `Transforms/UIScrollableTransform.cs` (97 lines)

A scrollable container. Currently delegates to base behavior.

### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Scrollable` | `bool` | `true` | Enable scrolling |
| `ScrollableX/Y` | `bool` | `true` | Per-axis scrolling |
| `ScrollableXMargin/YMargin` | `float` | `0.0` | Scroll margin |
| `ScrollableXMin/YMin` | `float` | `0.0` | Scroll range min |
| `ScrollableXMax/YMax` | `float` | `0.0` | Scroll range max |

### PlacementInfo

`UIScrollablePlacementInfo` — `BottomLeftOffset` property; `GetRelativeItemMatrix()` translates by offset.

### Layout Behavior

`OnResizeChildComponents` calls `base.OnResizeChildComponents()` — no custom layout logic yet.

---

## UIRotationTransform

**File**: `Transforms/UIRotationTransform.cs` (38 lines)

Adds rotation to the transform chain. **Not a UIBoundableTransform** — it has no size/anchors.

### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `DegreeRotation` | `float` | `0.0` | Rotation in degrees |

### CreateLocalMatrix
```csharp
RotationZ(DegreeRotation) * base.CreateLocalMatrix()
```

---

## UIDockableTransform

**File**: `UIDockableTransform1.cs` (lines 78+)

Supports interactive drag-and-drop docking. Extends `UIBoundableTransform` with:

### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `DragDropThreshold` | `float` | `0.25` | How close to edge for dock detection |
| `DropTarget` | `EDropLocationFlags` | `None` | Current hover dock position |

### Behavior

Calculates which edge a dragged item is near (`DragDrop(worldPoint)`) and sets `DropTarget` flags. Used for building dockable editor panels.

---

## UIChildPlacementInfo

**File**: `Transforms/UIChildPlacementInfo.cs` (21 lines)

Abstract base class for parent-specified child positioning.

```csharp
public abstract class UIChildPlacementInfo(UITransform owner) : XRBase
{
    public UITransform Owner { get; }
    public bool RelativePositioningChanged { get; set; }
    public abstract Matrix4x4 GetRelativeItemMatrix();
}
```

Each parent transform type creates a subclass via `VerifyPlacementInfo()`. The matrix from `GetRelativeItemMatrix()` is multiplied into the child's local transform during `CreateLocalMatrix()`.

### Subclasses

| Class | Parent Type | Key Property | Matrix Effect |
|-------|-------------|-------------|---------------|
| `UISplitChildPlacementInfo` | DualSplit, MultiSplit | `Offset` | Translate along split axis |
| `UIDockingPlacementInfo` | DockingRoot | `BottomLeft` | Translate to dock position |
| `UIListChildPlacementInfo` | List | `BottomOrLeftOffset` | Translate along list axis |
| `UIGridChildPlacementInfo` | Grid | `Row`, `Column`, `RowSpan`, `ColumnSpan` | (Grid uses it for cell lookup, not matrix) |
| `UIScrollablePlacementInfo` | Scrollable | `BottomLeftOffset` | Translate by scroll offset |
