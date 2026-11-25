# 02 — UI Transform Types Reference

Every UI element's position and size is determined by its transform. This document describes each transform type, its properties, and how it participates in layout.

File paths are relative to `XREngine.Runtime.Rendering/Scene/Components/UI/Core/`.

The canvas layout path does not call transform layout methods. `UILayoutSnapshot` maps each transform type to a `UILayoutOperator` and captures its values. `UILayoutValueKernel` computes measure and arrange from those values. `UILayoutCanvasSession` applies the accepted result. See [UI layout architecture](01-Architecture.md) and [retained execution](05-Retained-Execution.md).

---

## Inheritance Hierarchy

```
TransformBase
└── UITransform                     (base: 2D translation, scale, rotation)
    ├── UIBoundableTransform        (adds: size, anchors, margins, padding)
    │   ├── UICanvasTransform       (root: screen/camera/world space)
    │   ├── UIFittedTransform       (stretch/center/fill fit modes)
    │   ├── UIScrollableTransform   (scrollable container)
    │   ├── UIDualSplitTransform    (two-panel split)
    │   ├── UIMultiSplitTransform   (multi-panel split)
    │   ├── UIDockingRootTransform  (center/left/right/bottom dock)
    │   ├── UIListTransform         (vertical/horizontal list)
    │   ├── UIGridTransform         (row/column grid)
    │   │   └── UITreeTransform     (uses the grid operator)
    │   ├── UITreeItemTransform     (uses the boundable operator)
    │   ├── UITabTransform          (tabbed container, one child visible)
    │   └── UIDockableTransform     (drag-drop docking support)
    └── UIRotationTransform         (rotation-only wrapper, NOT boundable)
```

`UILayoutSnapshot` matches the exact type. A subclass of a listed type that is not in this tree must implement `IUILayoutValueAdapter` and declare the `Transform` or `Boundable` operator. Otherwise capture throws `NotSupportedException`.

---

## UITransform

**File**: `Transforms/UITransform.cs`

The non-bounded base class. Has position but no inherent size.

### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Translation` | `Vector2` | `(0,0)` | 2D position offset |
| `ActualLocalBottomLeftTranslation` | `Vector2` | `(0,0)` | Final computed position after layout |
| `DepthTranslation` | `float` | `0` | Z-offset for depth sorting |
| `Scale` | `Vector3` | `(1,1,1)` | Scale factor |
| `RotationRadians` | `float` | `0` | Z-axis rotation |
| `RotationDegrees` | `float` | `0` | `RotationRadians` in degrees |
| `Visibility` | `EVisibility` | `Visible` | Visible/Hidden/Collapsed |
| `ParentCanvas` | `UICanvasTransform?` | `null` | Root canvas (auto-set from parent chain) |
| `PlacementInfo` | `UIChildPlacementInfo?` | `null` | Parent-supplied positioning (lazy-created) |
| `StylingClass` | `string` | `""` | CSS class for styling |
| `StylingID` | `string` | `""` | CSS ID for styling |

### Layout State

| Field | Type | Description |
|-------|------|-------------|
| `_layoutVersion` | `uint` (volatile) | Increments on any layout-affecting change |
| `_lastMeasuredVersion` | `uint` | Snapshot after last measure |
| `_lastArrangedVersion` | `uint` | Snapshot after last arrange |
| `_desiredSize` | `Vector2` | Result of measure phase |
| `_lastMeasureConstraint` | `Vector2` | Available size from last measure |
| `_lastArrangeBounds` | `BoundingRectangleF` | Bounds from last arrange |

`ApplyCalculatedLayout` writes these values from an accepted kernel output.

### Layout Behavior

- **Measure**: Calls `UILayoutSystem.MeasureAcceptedOrDetached`. An attached transform requests canvas layout and returns its last accepted desired size. A detached tree measures with `UILayoutValueKernel`.
- **Arrange** and **FitLayout**: Request canvas layout through `UILayoutSystem.RequestAttachedLayout`. They do not set geometry.
- **Kernel operator**: `Transform`. The desired size is the largest child desired size. The transform receives its slot size and keeps `Translation` as its local position.
- **CreateLocalMatrix**: `Scale * RotationZ * Translation3D`

### Invalidation Triggers

| Properties | Method |
|-----------|--------|
| `Translation` | `InvalidateArrange()` |
| `Children` | `InvalidateMeasure()` |
| `Visibility`, `ParentCanvas`, `Parent`, `PlacementInfo`, `StylingClass`, `StylingID` | `InvalidateLayout()` |
| `DepthTranslation`, `Scale`, `RotationRadians` | Records a transform change. Does not invalidate layout. |

---

## UIBoundableTransform

**File**: `Transforms/UIBoundableTransform.cs`

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
| `CalcAutoWidthCallback` | `Func<UIBoundableTransform, float>?` | `null` | Custom auto-width calculator |
| `CalcAutoHeightCallback` | `Func<UIBoundableTransform, float>?` | `null` | Custom auto-height calculator |
| `ExcludeFromParentAutoCalcWidth` | `bool` | `false` | Skip this child when parent auto-sizes width |
| `ExcludeFromParentAutoCalcHeight` | `bool` | `false` | Skip this child when parent auto-sizes height |
| `BlocksInputBehind` | `bool` | `false` | Blocks UI input to elements behind this one |

Canvas layout does not call the auto-size callbacks. `UITextComponent` sets them, and the kernel measures the captured text input instead. Capture throws `NotSupportedException` for any other callback on an auto-sized axis.

### Anchor Behavior

When `MinAnchor == MaxAnchor` (point anchor):
- Size = `Width`/`Height`, else the text intrinsic size, else the measured child size. `Min`/`Max` constraints clamp it.
- Position = anchor * parent size + Translation - pivot * size + margin offset

When `MinAnchor != MaxAnchor` (stretched anchor):
- Size = (maxAnchor * parent + Width) - (minAnchor * parent + Translation) - leftMargin - rightMargin
- `Width`/`Height` move the max anchor edge. `Translation` moves the min anchor edge.
- Position = minAnchor * parent - pivot * size + leftMargin

### Layout Behavior

- **Kernel operator**: `Boundable`. Children use the padded region. The default desired size is the largest child size plus margins.
- **Measure**, **Arrange**, **FitLayout**: Same as `UITransform`. They request canvas layout. `UILayoutSystem.FitLayout` also marks the canvas layout invalid.
- **ArrangeChildren**, **MeasureChildrenWidth**, **MeasureChildrenHeight**, **GetActualBounds** (virtual): The canvas layout path does not call these methods or their overrides.
- **OnLocalMatrixChanged**: Does not start layout.

### CreateLocalMatrix
```
Translation(ActualLocalBottomLeftTranslation, DepthTranslation)
  × PlacementInfo.GetRelativeItemMatrix()    (if exists)
  × Pivot → Scale → Rotation → Un-Pivot     (if non-identity)
```

### Invalidation Triggers

| Properties | Method |
|-----------|--------|
| `Width`, `Height`, `Min/MaxWidth/Height`, `Margins`, `Padding`, `NormalizedPivot`, `CalcAutoWidthCallback`, `CalcAutoHeightCallback`, `ExcludeFromParentAutoCalcWidth/Height` | `InvalidateMeasure()` |
| `MinAnchor`, `MaxAnchor` | `InvalidateArrange()` |
| (Inherited from UITransform) | See `UITransform` |

---

## UICanvasTransform

**File**: `Transforms/UICanvasTransform.cs`

Root of a UI hierarchy. Defines draw space and manages layout scheduling.

### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `DrawSpace` | `ECanvasDrawSpace` | `Screen` | Screen / Camera / World |
| `CameraSpaceCamera` | `XRCamera?` | `null` | Camera for camera-space rendering |
| `CameraDrawSpaceDistance` | `float` | `1.0` | Distance from camera |
| `IsLayoutInvalidated` | `bool` | `true` | Thread-safe flag: layout needs recalc |
| `IsUpdatingLayout` | `bool` | `false` | Thread-safe flag: layout in progress |
| `DpiScale` | `float` | `1.0` | Explicit positive text scale in canvas pixels |

### Layout Behavior

- `UpdateLayout()` and `UpdateLayoutAsync()` request the bounded snapshot worker.
- `PumpLayoutResults()` accepts a completed result only after dependency checks.
- The serial oracle and worker use `UILayoutValueKernel`.
- `InvalidateLayout()` records dirty state for the next request.
- Root bounds and DPI changes use the same numeric layout kernel.
- Nested canvases share the outer layout owner.

See [retained execution](05-Retained-Execution.md) for worker quotas and lifetime.

### CreateWorldMatrix
- **Screen space**: `Matrix4x4.Identity`
- **Camera space**: Positioned at camera's bottom-left at specified depth, oriented with camera
- **World space**: Uses standard parent-chain world matrix

### Size Management

`SetSize(Vector2)` sets Width, Height, and anchors to (0,0) with pivot at (0,0). `UICanvasComponent.ResizeScreenSpace()` calls it when the viewport dimensions change. `UICanvasComponent.ResizeCameraSpace()` calls it with the frustum size at `CameraDrawSpaceDistance`.

---

## UIDualSplitTransform

**File**: `Transforms/UIDualSplitTransform.cs`

Splits available space between two children along a horizontal or vertical axis.

### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `VerticalSplit` | `bool` | `false` | `true` splits along Y (first child on top). `false` splits along X (first child on the left). |
| `SplitPercent` | `float` | `0.5` | First child's share of the axis (clamped to 0–1). Used when `FirstFixedSize` is `null`. |
| `FirstFixedSize` | `bool?` | `null` | `null` uses `SplitPercent`. `true` gives the first child a fixed size. `false` gives the second child a fixed size. |
| `FixedSize` | `float?` | `null` | Size of the fixed child. If `null`, the fixed child uses its measured size. |
| `SplitterSize` | `float` | `0.0` | Gap between panels |
| `CanUserResize` | `bool` | `true` | Allow interactive resize |

### PlacementInfo

`UIDualSplitTransform.UISplitChildPlacementInfo` — `UILayoutCanvasSession` writes the accepted offset along the split axis to `Offset`. `GetRelativeItemMatrix()` translates the child by `Offset` along that axis.

### Layout Behavior

The kernel operator is `DualSplit`. With one child, the child gets the full padded region. With two children, the kernel divides the padded region along the split axis and arranges each child in its region.

---

## UIMultiSplitTransform

**File**: `Transforms/UIMultiSplitTransform.cs`

Splits space into 2 or 3 regions with multiple arrangement patterns.

### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Arrangement` | `UISplitArrangement` | `LeftMiddleRight` | Layout pattern |
| `FixedSizeFirst` | `float?` | `null` | Fixed size for first region (must be finite and nonnegative) |
| `FixedSizeSecond` | `float?` | `null` | Fixed size for last region (must be finite and nonnegative) |
| `SplitPercentFirst` | `float` | `0.33` | Split point for first boundary (clamped to 0–1) |
| `SplitPercentSecond` | `float` | `0.66` | Split point for second boundary (clamped to 0–1) |
| `SplitterSize` | `float` | `0.0` | Gap between regions (must be finite and nonnegative) |
| `CanUserResize` | `bool` | `true` | Allow interactive resize |

### Arrangements

| Value | Regions | Axis |
|-------|---------|------|
| `LeftMiddleRight` | 3 horizontal | X |
| `TopMiddleBottom` | 3 vertical | Y |
| `LeftRight` | 2 horizontal | X |
| `TopBottom` | 2 vertical | Y |

### Size Calculation

The kernel operator is `MultiSplit`. Fixed sizes take priority. Split percentages give the size of each region that has no fixed size. In a three-region split, the middle region gets the remaining space. With one child, the child gets the full padded region. A three-region arrangement with two children uses two regions. The kernel rejects more than three children, three children in a two-region arrangement, and `SplitPercentSecond < SplitPercentFirst` in a three-region split.

### PlacementInfo

`UIMultiSplitTransform.UISplitChildPlacementInfo` — a separate nested class with the same pattern as the dual split. `Offset` translates along the split direction. `Vertical` is `true` for `TopMiddleBottom` and `TopBottom`.

---

## UIDockingRootTransform

**File**: `Transforms/UIDockingRootTransform.cs`

Arranges up to 4 children: center, left, right, bottom.

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

The kernel operator is `DockingRoot`. Regions are (x, y, width, height) in the padded region, with the origin at the bottom-left:

```
Center: (LeftSizeWidth, BottomSizeHeight, width - LeftSizeWidth - RightSizeWidth, height - BottomSizeHeight)
Left:   (0, BottomSizeHeight, LeftSizeWidth, height - BottomSizeHeight)
Right:  (width - RightSizeWidth, BottomSizeHeight, RightSizeWidth, height - BottomSizeHeight)
Bottom: (0, 0, width, BottomSizeHeight)
```

The kernel rejects more than four children.

### PlacementInfo

`UIDockingPlacementInfo` — `UILayoutCanvasSession` writes the accepted region position to `BottomLeft`. `GetRelativeItemMatrix()` translates by `BottomLeft`.

### Auto-Creation

`UIDockingRootComponent` creates four `UIBoundableTransform` children when it is constructed.

---

## UIListTransform

**File**: `Arrangements/UIListTransform.cs` (with `UIListTransform.Virtual.cs`)

Arranges children sequentially — vertical or horizontal.

### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ItemSize` | `float?` | `null` | Uniform size per item (`null` = use child's own size) |
| `DisplayHorizontal` | `bool` | `false` | Horizontal (true) or vertical (false) list |
| `ItemSpacing` | `float` | `0.0` | Gap between items |
| `ItemAlignment` | `EListAlignment` | `TopOrLeft` | TopOrLeft / Centered / BottomOrRight |
| `ContentScrollOffset` | `float` | `0.0` | Scroll offset along the list axis |
| `VirtualProvider` | `IVirtualListProvider?` | `null` | Provider for virtual rows |
| `VirtualOverscan` | `int` | `2` | Extra virtual rows outside the viewport (clamped to 0–256) |
| `Virtual` | `bool` | `false` | Legacy virtual flag. The layout kernel does not read it. |
| `UpperVirtualBound` | `float` | `0.0` | Legacy upper bound. The layout kernel does not read it. |
| `LowerVirtualBound` | `float` | `0.0` | Legacy lower bound. The layout kernel does not read it. |

### Custom Measure

The kernel operator is `List`:
- For the list axis: **sums** `ItemSize` or each child's desired size, plus spacing (not max)
- For the cross axis: uses the largest child size
- With a `VirtualProvider`, the list-axis size is `VirtualContentExtent`

### Custom Arrange

The kernel uses three alignment strategies:

**TopOrLeft**: Starts from top (vertical) or left (horizontal), decrements Y or increments X.

**Centered**: Calculates total size, centers offset, then lays out sequentially.

**BottomOrRight**: Starts from bottom/right, moves opposite direction.

Each child gets `ItemSize ?? DesiredSize` along the list axis and the full region along the cross axis. `ContentScrollOffset` shifts the start position.

### Virtualization

With a `VirtualProvider`, the list realizes one row subtree for each visible item, plus `VirtualOverscan` rows. `SynchronizeVirtualRows` runs on the owner during capture. The kernel places each row from its synchronized start and extent.

### PlacementInfo

`UIListChildPlacementInfo` — `Index` and `BottomOrLeftOffset` properties. `UILayoutCanvasSession` writes the accepted offset along the list axis to `BottomOrLeftOffset`. `GetRelativeItemMatrix()` translates along the list axis.

---

## UIGridTransform

**File**: `Arrangements/UIGridTransform.cs`

Arranges children in a grid defined by row and column sizing definitions.

### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Rows` | `EventList<UISizingDefinition>` | `[]` | Row definitions |
| `Columns` | `EventList<UISizingDefinition>` | `[]` | Column definitions |
| `InvertY` | `bool` | `false` | Invert vertical ordering |

### Sizing Modes (UISizingDefinition)

Each `UISizingDefinition` has a `Value` and optional `Min` and `Max`. Each is a `UISizingValue` with a `Mode` (`ESizingMode`) and a `Value` (`float`).

| Mode | Behavior |
|------|----------|
| `Fixed` | Exact pixel size |
| `Auto` | Size to content (max child size in that row/col) |
| `Proportional` | Share remaining space proportionally |

### Layout Algorithm

The kernel operator is `Grid` (`UILayoutGridKernel`). A grid with children must have at least one row and one column.

1. **Fixed pass**: Set fixed track sizes
2. **Auto pass**: Grow auto tracks to fit the desired size of the children that span them
3. **Remaining space**: Subtract fixed + auto from parent, distribute to proportional tracks
4. **Arrange pass**: For each child, compute (x, y, width, height) from its row, column, and spans, and arrange the child in that cell

### PlacementInfo

`UIGridChildPlacementInfo` — `Row`, `Column`, `RowSpan`, `ColumnSpan`, and `Offset` properties. `Row` and `Column` must be 0 or more. Spans must be 1 or more. `UILayoutCanvasSession` writes the accepted cell position to `Offset`, and `GetRelativeItemMatrix()` translates by `Offset`.

---

## UITabTransform

**File**: `Transforms/UIDockableTransform1.cs`

Shows one child at a time.

### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `SelectedIndex` | `int` | `0` | Currently visible child (clamped to the child range) |
| `TabHeight` | `float` | `30.0` | Tab bar height. The layout kernel does not read it. |

### Layout Behavior

The kernel operator is `Tab`. Every child gets the full padded region. Only the child at `SelectedIndex` is visible in the layout output. Layout does not change the `Visibility` property of the children.

---

## UIFittedTransform

**File**: `Transforms/UIFittedTransform.cs`

Custom fit behavior within parent bounds.

### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `FitType` | `UIFittedTransform.EFitType` | `None` | Fit mode |

### Fit Modes

| Mode | Behavior |
|------|----------|
| `None` | Standard bounds calculation |
| `Stretch` | Size = parent extents (fill completely) |
| `Center` | Maintain aspect ratio, fit inside parent (letterbox) |
| `Fill` | Maintain aspect ratio, fill parent (crop) |

### Extension Point

The kernel operator is `Fitted`. After the standard bounds calculation, the kernel changes the size based on `FitType`.

---

## UIScrollableTransform

**File**: `Transforms/UIScrollableTransform.cs`

A scrollable container. The kernel shifts children by `ScrollOffset`.

### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ScrollOffset` | `Vector2` | `(0,0)` | Positive X scrolls right and positive Y scrolls down. Clamped to `MaxScrollOffset` after accepted layout. |
| `ContentExtent` | `Vector2` | `(0,0)` | Accepted logical content size (read-only) |
| `ViewportExtent` | `Vector2` | `(0,0)` | Accepted viewport size (read-only) |
| `MaxScrollOffset` | `Vector2` | `(0,0)` | `ContentExtent - ViewportExtent`, not below zero (read-only) |
| `Scrollable` | `bool` | `true` | Enable scrolling |
| `ScrollableX/Y` | `bool` | `true` | Per-axis scrolling |
| `ScrollableXMargin/YMargin` | `float` | `0.0` | Scroll margin |
| `ScrollableXMin/YMin` | `float` | `0.0` | Scroll range min |
| `ScrollableXMax/YMax` | `float` | `0.0` | Scroll range max |

### PlacementInfo

`UIScrollableTransform` does not assign a placement info to its children. The kernel puts the scroll offset into each child's position.

### Layout Behavior

The kernel operator is `Scrollable`. Every child gets the full padded region, shifted by `ScrollOffset`. A child `UIListTransform` with a `VirtualProvider` is not shifted; it receives the scroll offset as its `ContentScrollOffset`. `SynchronizeScroll` updates the scroll limits on the owner during capture. `ScrollIntoView` moves the viewport to include a content rectangle.

---

## UIRotationTransform

**File**: `Transforms/UIRotationTransform.cs`

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

**File**: `Transforms/UIDockableTransform1.cs`

Supports interactive drag-and-drop docking. Extends `UIBoundableTransform` with:

### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `DragDropThreshold` | `float` | `0.25` | How close to edge for dock detection |
| `DropTarget` | `EDropLocationFlags` | `None` | Current hover dock position |

### Behavior

Calculates which edge a dragged item is near (`DragDrop(worldPoint)`) and sets `DropTarget` flags. Used for building dockable editor panels. The `Dock*` methods that `Drop()` calls are empty.

---

## UIChildPlacementInfo

**File**: `Transforms/UIChildPlacementInfo.cs`

Abstract base class for parent-specified child positioning.

```csharp
public abstract class UIChildPlacementInfo(UITransform owner) : XRBase, ITransformChildPlacementInfo
{
    public UITransform Owner { get; }
    public bool RelativePositioningChanged { get; set; } // default true
    public abstract Matrix4x4 GetRelativeItemMatrix();
}
```

Each parent transform type creates a subclass via `VerifyPlacementInfo()`. The matrix from `GetRelativeItemMatrix()` is multiplied into the child's local transform during `CreateLocalMatrix()`. Any other property change sets `RelativePositioningChanged` to `true`.

### Subclasses

| Class | Parent Type | Key Property | Matrix Effect |
|-------|-------------|-------------|---------------|
| `UIDualSplitTransform.UISplitChildPlacementInfo` | DualSplit | `Offset` | Translate along split axis |
| `UIMultiSplitTransform.UISplitChildPlacementInfo` | MultiSplit | `Offset` | Translate along split axis |
| `UIDockingPlacementInfo` | DockingRoot | `BottomLeft` | Translate to dock position |
| `UIListChildPlacementInfo` | List | `BottomOrLeftOffset` | Translate along list axis |
| `UIGridChildPlacementInfo` | Grid | `Row`, `Column`, `RowSpan`, `ColumnSpan`, `Offset` | Translate by `Offset` to the cell position |
