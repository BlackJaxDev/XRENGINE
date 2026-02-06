# 03 — Rendering Pipeline

How laid-out UI elements get drawn to the screen (or into the world).

---

## Overview

```
XRViewport
  └── UICanvasComponent (per viewport)
        ├── UICanvasTransform   — owns the UI hierarchy
        ├── VisualScene2D       — quadtree of RenderInfo2D items
        ├── Camera2D            — orthographic camera for screen-space
        └── RenderPipeline      — UserInterfaceRenderPipeline instance
```

Each frame:
1. **Layout** — UICanvasTransform.UpdateLayout() runs the two-phase measure/arrange
2. **Collect** — VisualScene2D collects items visible to Camera2D
3. **Render** — RenderPipeline draws collected items

---

## Screen-Space Path

### Frame Timeline

```
Engine Frame Loop
  ├── CollectVisible phase
  │     └── UICanvasComponent.CollectVisibleItemsScreenSpace()
  │           ├── CanvasTransform.UpdateLayout()
  │           │     └── UILayoutSystem.UpdateCanvasLayout(canvas)
  │           │           ├── MeasureTransform(canvas, ...)
  │           │           └── ArrangeTransform(canvas, ...)
  │           └── VisualScene2D.CollectRenderedItems(commands, Camera2D, ...)
  │                 └── QuadTree queries for items in camera frustum
  │
  ├── SwapBuffers phase
  │     └── UICanvasComponent.SwapBuffersScreenSpace()
  │           ├── MeshRenderCommands.SwapBuffers()
  │           └── VisualScene2D.GlobalSwapBuffers()
  │           
  │     └── TransformBase.RecalculateMatrices() (deferred from MarkLocalModified)
  │           └── CreateLocalMatrix()
  │           └── OnLocalMatrixChanged() → OnResizeChildComponents() [OLD PATH]
  │
  └── Render phase
        └── UICanvasComponent.RenderScreenSpace(viewport, outputFBO)
              └── RenderPipeline.Render(VisualScene2D, Camera2D, ...)
```

### Key Timing Issue

Layout runs during **CollectVisible**, but `MarkLocalModified(deferred: true)` defers matrix recalculation to **SwapBuffers**. So:

1. `ArrangeBoundable` sets `ActualLocalBottomLeftTranslation` → `MarkLocalModified(deferred: true)`
2. SwapBuffers triggers `RecalculateMatrices` → `CreateLocalMatrix` → `OnLocalMatrixChanged`
3. `OnLocalMatrixChanged` calls `OnResizeChildComponents` (OLD path) → runs ANOTHER layout pass
4. This happens AFTER collection, so items may be in the wrong position for the current frame

---

## Camera-Space & World-Space Path

For non-screen-space canvases, layout runs during `CollectVisible` via an event subscription:

```csharp
// In UICanvasComponent.OnComponentActivated():
Engine.Time.Timer.CollectVisible += UpdateLayoutWorldSpace;
```

```
UpdateLayoutWorldSpace()
  ├── Guard: DrawSpace != Screen && IsActive
  ├── CanvasTransform.UpdateLayout()
  └── ForceRenderMatrixUpdatesRecursive()
        └── RecalculateMatrices(false, true) — immediate, not deferred
        └── Recurse on all children
```

**Critical difference**: World-space forces immediate matrix updates via `ForceRenderMatrixUpdatesRecursive`. Screen-space relies on deferred SwapBuffers.

---

## VisualScene2D — The Quadtree

`VisualScene2D` manages a quadtree of `RenderInfo2D` items. Each UI component that renders (text, material, mesh, etc.) creates a `RenderInfo2D` and registers it with the visual scene.

### Quadtree Structure

```
VisualScene2D
  └── RenderTree: QuadTree<RenderInfo2D>
        ├── Spatial index for O(log n) visibility queries
        ├── Each item has Bounds (BoundingRectangleF) and CullingVolume
        └── Remade when canvas resizes (ResizeScreenSpace)
```

### Quadtree Lifecycle

1. **Created**: First `ResizeScreenSpace()` call (on canvas ActualSize change)
2. **Remade**: `VisualScene2D.RenderTree.Remake(bounds)` when canvas size changes
3. **Items added**: When a UI component with rendering creates its `RenderInfo2D`
4. **Items updated**: When `RemakeAxisAlignedRegion()` updates `AxisAlignedRegion` on `UIBoundableTransform`
5. **Queried**: `CollectRenderedItems()` gathers items within camera frustum

### Item Positioning in Quadtree

Each `UIBoundableTransform` maintains:

| Property | Updated When | Used For |
|----------|-------------|----------|
| `AxisAlignedRegion` | `RemakeAxisAlignedRegion(ActualSize, WorldMatrix)` | Quadtree spatial bounds |
| `RegionWorldTransform` | Same call | World-space transform of region |

`RemakeAxisAlignedRegion` runs:
- In `OnResizeActual` — when layout assigns new size
- In `OnWorldMatrixChanged` — when parent or self world matrix changes

The region is computed as: transform `(0,0)` and `(1,1)` by `Scale(actualSize) * worldMatrix`, take axis-aligned bounding box.

### UpdateRenderInfoBounds

UI components call `UpdateRenderInfoBounds()` to sync their render info with the quadtree:

```csharp
void UpdateRenderInfoBounds(params RenderInfo[] infos)
{
    foreach info:
      if RenderInfo2D and DrawSpace == Screen:
        renderInfo2D.CullingVolume = AxisAlignedRegion;
      if RenderInfo3D and DrawSpace != Screen:
        renderInfo3D.CullingOffsetMatrix = RegionWorldTransform;
        renderInfo3D.LocalCullingVolume = AABB(width, height, 0.1);
}
```

---

## Camera2D — Orthographic Projection

For screen-space canvases:

```csharp
void ResizeScreenSpace(BoundingRectangleF bounds)
{
    // Remake quadtree to match new canvas size
    VisualScene2D.RenderTree.Remake(bounds);
    
    // Configure ortho camera: origin bottom-left, size = canvas size
    orthoParams.SetOriginBottomLeft();
    orthoParams.Resize(bounds.Width, bounds.Height);
    // NearZ = -0.5, FarZ = 0.5 (defaults)
    
    // Notify render pipeline of size change
    _renderPipeline.ViewportResized(canvasTransform.ActualSize);
}
```

The camera's projection maps (0,0) → bottom-left and (Width, Height) → top-right, matching the UI coordinate system.

---

## ResizeScreenSpace — When It Runs

`ResizeScreenSpace` is called from `OnCanvasTransformPropertyChanged`:

```csharp
private void OnCanvasTransformPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
{
    switch (e.PropertyName)
    {
        case "ActualLocalBottomLeftTranslation":
        case "ActualSize":
            ResizeScreenSpace(CanvasTransform.GetActualBounds());
            break;
    }
}
```

This fires when:
1. Viewport resizes → engine sets canvas Width/Height → layout runs → ActualSize changes
2. **Every frame if the SetField guard is missing** (see Known Issues)

The performance guard in `OnResizeActual` (comparing vectors before calling property setters) prevents this from firing every frame when bounds haven't actually changed.

---

## Hit Testing

`UICanvasComponent` provides hit-testing for input:

```csharp
UIComponent? FindDeepestComponent(Vector2 normalizedViewportPosition)
UIComponent?[] FindDeepestComponents(Vector2 normalizedViewportPosition)
```

These query the quadtree: `VisualScene2D.RenderTree.Collect()` finds items whose bounds contain the point, then filters by `CullingVolume.Contains()`, ordered by depth.

---

## Render Pipeline

`UICanvasComponent` uses `UserInterfaceRenderPipeline` via `XRRenderPipelineInstance`:

```csharp
private readonly XRRenderPipelineInstance _renderPipeline = new() 
{ 
    Pipeline = new UserInterfaceRenderPipeline() 
};
```

Rendering happens in `RenderScreenSpace`:
```csharp
_renderPipeline.Render(VisualScene2D, Camera2D, null, viewport, outputFBO, null, false, false);
```

The render pipeline processes all collected render commands (text, materials, meshes) and draws them to the viewport's framebuffer using the orthographic camera projection.

---

## Summary of Data Flow

```
Property Change
  → InvalidateLayout()
  → _layoutVersion++
  → canvas.IsLayoutInvalidated = true

Next CollectVisible:
  → UpdateLayout()
  → MeasureTransform (bottom-up: compute DesiredSize)
  → ArrangeTransform (top-down: compute ActualSize + ActualLocalBottomLeftTranslation)
  → MarkLocalModified(deferred) on changed transforms
  → CollectRenderedItems from quadtree

SwapBuffers:
  → RecalculateMatrices (deferred transforms)
  → CreateLocalMatrix → OnLocalMatrixChanged → OnResizeChildComponents [OLD PATH]
  → Quadtree items updated via RemakeAxisAlignedRegion

Render:
  → RenderPipeline draws collected commands with Camera2D projection
```
