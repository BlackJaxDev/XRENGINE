# Transform Architecture

XRENGINE stores spatial state inside `TransformBase` derivatives that hang off every `SceneNode`. Transforms maintain the local/world matrix stack, publish render-thread data, and coordinate hierarchy changes across gameplay, editor, and render threads. This guide explains how the pieces fit together so you can extend or consume the transform system safely.

## Core Responsibilities
- Own the local transform state (position, rotation, scale) relative to the parent and expose recalculated matrices without exposing raw setters.
- Produce the world matrix that includes every ancestor transform and notify the active `XRWorldInstance` when that result changes.
- Publish a render-safe matrix snapshot so the render thread can fetch transform data without locking gameplay code.
- Relay lifecycle events (`RenderMatrixChanged`, `WorldMatrixChanged`, etc.) so components and systems can respond as soon as matrices are updated.

## Matrix Stack
**Local Matrix**
- `LocalMatrix` represents the transform relative to the parent. Subclasses implement `CreateLocalMatrix` to build their TRS (translation/rotation/scale) or custom layouts.
- `MarkLocalModified` flags the local matrix as dirty. If `ImmediateLocalMatrixRecalculation` is true (default), `RecalcLocal` runs immediately and also propagates `MarkWorldModified`.

**World Matrix**
- `WorldMatrix` multiplies the local matrix against the parent via `CreateWorldMatrix` (default: `LocalMatrix * Parent.WorldMatrix`).
- `MarkWorldModified` queues the transform with `XRWorldInstance.AddDirtyTransform`, which groups dirty transforms by depth. `XRWorldInstance.PostUpdate` consumes that queue and executes `RecalculateMatrixHierarchy` for each depth bucket.
- `OnWorldMatrixChanged` pushes the result to the render queue (`XRWorldInstance.EnqueueRenderTransformChange`) and fires `WorldMatrixChanged` so dependent systems can respond.

**Render Matrix**
- The render thread consumes `RenderMatrix`, a cached snapshot written during `XRWorldInstance.GlobalSwapBuffers`. That method dequeues pending tuples and calls `TransformBase.SetRenderMatrix`, ensuring render data updates only when the engine swaps frame buffers.
- `SetRenderMatrix` optionally cascades updates to children using the loop type configured in `Engine.Rendering.Settings.RecalcChildMatricesLoopType`, keeping render matrices in sync throughout the hierarchy.
- Helpers such as `GetWorldTranslation`, `GetWorldForward`, and `GetWorldRotation` automatically switch between `WorldMatrix` and `RenderMatrix` depending on `Engine.IsRenderThread`, which avoids branching in calling code.

**Inverse Matrices and Direction Vectors**
- `InverseLocalMatrix` and `InverseWorldMatrix` are regenerated alongside their forward counterparts (`RecalcLocalInv`, `RecalcWorldInv`) and fire change events for systems that need quick inverse access (physics, skeletal animation).
- Convenience vectors (`LocalForward`, `WorldUp`, `RenderRight`, etc.) are derived from the respective matrices and normalised for fast directional queries.

## Dirty Flag and Recalculation Flow
1. Gameplay or editor code mutates transform properties and calls `MarkLocalModified` or `MarkWorldModified`.
2. The transform is added to `_invalidTransforms` inside `XRWorldInstance`. Depth bucketing guarantees parents process before children.
3. During `PostUpdate`, the engine iterates each depth and calls `RecalculateMatrixHierarchy` using the configured loop strategy (sequential, parallel, or asynchronous). That method recalculates this transform and optionally its children.
4. `OnWorldMatrixChanged` enqueues the world matrix for render publication. During `GlobalSwapBuffers`, the engine applies all queued render matrices, fires `RenderMatrixChanged`, and propagates children if requested.
5. Systems that depend on transform data (rendering, physics, UI) listen to these events or access the thread-appropriate helpers.

When immediate results are required (for example, before physics queries), you can call `RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true)` directly. This forces recalculation on the current thread and updates the render matrix synchronously for the current transform only.

## Parenting and Hierarchy Management
- `SetParent` optionally preserves the world matrix. Deferred changes enqueue into a static `_parentsToReassign` queue so any thread can request re-parenting. The queue is processed on the main update when the engine is in a safe state.
- `ProcessParentReassignments` should be called during world maintenance (the engine does this automatically) to finalise pending parent swaps.
- `SceneNode.SetTransform` replaces the transform instance attached to a node. Flags such as `RetainCurrentParent`, `RetainCurrentChildren`, and `RetainedChildrenMaintainWorldTransform` let you migrate relationships without teleporting nodes or losing hierarchy data.

## Render Publication and Thread Safety
- World matrices are produced on the simulation thread; render matrices update on the render thread during buffer swaps. This separation prevents contention while still delivering deterministic snapshots per frame.
- Components and subsystems subscribe to `RenderMatrixChanged` to receive the frame-ready value. For example, render components update vertex buffers using this event without polling the transform every frame.
- Use `TransformBase.ProcessParentReassignments` and the render queue from the engine rather than writing ad-hoc threading code; these built-in mechanisms guarantee consistent sequencing across gameplay and rendering.

## Creating Custom Transforms
- Derive from `TransformBase` (or existing subclasses like `Transform`, `UITransform`, `OrbitTransform`) and override `CreateLocalMatrix`. For specialised behaviour you can also override `CreateWorldMatrix`, `TryCreateInverseLocalMatrix`, or `RenderDebug`.
- Whenever you mutate internal state, call `MarkLocalModified` or `MarkWorldModified` so the engine can enqueue recalculations. Skipping these calls will leave world/render matrices stale.
- If you maintain additional gizmos or cached data, hook into the matrix change events (`OnLocalMatrixChanged`, `OnWorldMatrixChanged`) to synchronise them.

## Tips for Consumers
- Prefer the thread-aware helpers (`GetWorldTranslation`, `GetWorldRotation`, etc.) when code may execute on either simulation or render threads.
- Use `FindChild`, `FindDescendant`, and `Children` only within locks or by relying on engine-provided iterators; the underlying `EventList` is thread-safe but still requires careful traversal in multithreaded code.
- When preserving world space during parent changes, use the `preserveWorldTransform` flag provided by `SetParent` rather than manually computing inverse matrices.

## Related Documentation
- [Scene Architecture](scene.md)
- [Component Architecture](components.md)
- [Rendering Architecture](rendering.md)
