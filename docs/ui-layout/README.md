# XREngine UI Layout System — Complete Documentation

This directory contains comprehensive documentation for the XREngine UI layout system architecture, covering:

| Document | Description |
|----------|-------------|
| [01-Architecture.md](01-Architecture.md) | Core architecture: two-phase measure/arrange, dual execution paths, canvas entry points, invalidation/dirty tracking |
| [02-Transform-Types.md](02-Transform-Types.md) | Reference for every UI transform type: properties, overrides, and child arrangement behavior |
| [03-Rendering-Pipeline.md](03-Rendering-Pipeline.md) | How laid-out UI reaches the screen: UICanvasComponent → VisualScene2D → Quadtree → RenderInfo2D |
| [04-Known-Issues.md](04-Known-Issues.md) | Architectural conflicts, performance traps, and outstanding bugs with analysis and suggested fixes |

## Progress Update (2026-02-06)

- Issues 1, 3, and 4 are fixed; issue 2 (camera WASD/mouse interaction) is still active.
- Current diagnostics show `IsHoveringUI` is false and `_rightClickDragging` is true, so UI hover gating is not the blocker.
- No `MoveForward` log entries appeared in the latest run, suggesting key state callbacks are not firing or movement is not applied.
- Added temporary debug logging to trace key presses and mouse deltas; see [XRENGINE/Scene/Components/Pawns/FlyingCameraPawnBaseComponent.cs](../../XRENGINE/Scene/Components/Pawns/FlyingCameraPawnBaseComponent.cs) and [XRENGINE/Scene/Components/Pawns/FlyingCameraPawn.cs](../../XRENGINE/Scene/Components/Pawns/FlyingCameraPawn.cs).

## Quick Orientation

The UI layout system lives in:

```
XRENGINE/Scene/Components/UI/Core/
├── Transforms/
│   ├── UILayoutSystem.cs          ← Centralized static layout engine
│   ├── UITransform.cs             ← Base class for all UI transforms
│   ├── UIBoundableTransform.cs    ← Base for bounded (sized) UI elements
│   ├── UICanvasTransform.cs       ← Root canvas (screen/camera/world)
│   ├── UIDualSplitTransform.cs    ← Two-panel split
│   ├── UIMultiSplitTransform.cs   ← Multi-panel split
│   ├── UIDockingRootTransform.cs  ← Center/left/right/bottom docking
│   ├── UIFittedTransform.cs       ← Stretch/center/fill fitting
│   ├── UIScrollableTransform.cs   ← Scrollable container
│   ├── UIRotationTransform.cs     ← Rotation wrapper
│   ├── UIChildPlacementInfo.cs    ← Abstract placement info base
│   ├── UIDockableTransform.cs     ← Commented-out legacy code
│   └── UIDockableTransform1.cs    ← UITabTransform + UIDockableTransform (active)
├── Arrangements/
│   ├── UIListTransform.cs         ← Vertical/horizontal list layout
│   └── UIGridTransform.cs         ← Row/column grid layout
```

Rendering integration:
```
XRENGINE/Scene/Components/Pawns/
└── UICanvasComponent.cs           ← Manages VisualScene2D, Camera2D, render pipeline
```

## Key Concept: Two Execution Paths

The layout system has **two competing execution paths** that both run:

1. **NEW Path** — `UILayoutSystem.UpdateCanvasLayout()` → Measure → Arrange → `ArrangeChildren()` virtual
2. **OLD Path** — `OnLocalMatrixChanged()` → `OnResizeChildComponents()` → `FitLayout()` on each child

Both paths currently execute. The NEW path runs first (during `CollectVisibleItems`), then the OLD path triggers whenever `MarkLocalModified()` causes deferred matrix recalculation and `OnLocalMatrixChanged` fires.

See [04-Known-Issues.md](04-Known-Issues.md) for the implications of this dual-path architecture.
