# Physics Debug Frame

This document describes how physics backends publish debug visualization and how the renderer draws it.

## Purpose

PhysX and Jolt produce debug points, lines, and triangles. The engine copies these primitives into one backend-neutral frame per simulation step. Each world uploads a frame one time and draws it in every view. The cost must stay near the cost of an equivalent instanced debug batch.

The visualization does not change simulation behavior. It does not silently reduce the requested detail. When a budget drops primitives, the engine reports the drop.

## Type And File Map

| Type | File | Responsibility |
|---|---|---|
| `PhysicsDebugFrame` | `XREngine.Runtime.Core/Scene/Physics/Debug/PhysicsDebugFrame.cs` | Read-only view of one published generation: point, line, and triangle spans, generation, source, depth mode, bounds, telemetry. |
| `PhysicsDebugPoint`, `PhysicsDebugLine`, `PhysicsDebugTriangle` | `XREngine.Runtime.Core/Scene/Physics/Debug/` | Packed GPU-ready records with `Pack = 4`. Color is RGBA8 UNORM in a `uint`. |
| `PhysicsDebugColor` | `PhysicsDebugColor.cs` | Packs `ColorF4` into the RGBA8 layout. |
| `PhysicsDebugFramePublisher` | `PhysicsDebugFramePublisher.cs` | Lock-free, single-producer publisher with three reusable slots. |
| `PhysicsDebugFrameWriter` | `PhysicsDebugFrameWriter.cs` | Reusable writer for one slot. Applies the budget. |
| `PhysicsDebugFrameLease` | `PhysicsDebugFrameLease.cs` | Pins a published slot while a consumer reads it. |
| `PhysicsDebugFrameStorage` | `PhysicsDebugFrameStorage.cs` | Slot arrays, counts, drop counters, bounds, and timing. |
| `PhysicsDebugBudget` | `PhysicsDebugBudget.cs` | Point, line, triangle, and byte limits. |
| `PhysicsDebugFrameTelemetry` | `PhysicsDebugFrameTelemetry.cs` | Allocation-free counters and timings for one generation. |
| `PhysicsDebugGeometryWriter` | `PhysicsDebugGeometryWriter.cs` | Shared helpers that write spheres, capsules, boxes, axes, and AABBs into a writer. |
| `PhysicsDebugSource`, `PhysicsDebugDepthMode` | `PhysicsDebugSource.cs`, `PhysicsDebugDepthMode.cs` | Backend identity (`PhysX`, `Jolt`) and depth category (`DepthTested`, `Overlay`). |
| `AbstractPhysicsScene.DebugFrames` | `XREngine.Runtime.Core/Scene/Physics/AbstractPhysicsScene.cs` | The publisher that each physics scene owns. |
| `PhysxDebugFrameAdapter` | `XREngine.Runtime.Physics.PhysX/Scene/Physics/Physx/PhysxDebugFrameAdapter.cs` | Copies the native `PxRenderBuffer` into a writer. |
| `JoltScene.PublishDebugFrame`, `JoltEngineDebugRenderer` | `XREngine.Runtime.Physics.Jolt/Scene/Physics/Jolt/` | Build the Jolt frame through the same writer. |
| `PhysicsDebugFrameRenderer` | `XREngine.Runtime.Rendering/Rendering/Physics/DebugVisualization/PhysicsDebugFrameRenderer.cs` | World-owned render-thread consumer. |
| `InstancedDebugVisualizer` | `XREngine.Runtime.Rendering/Rendering/Physics/DebugVisualization/InstancedDebugVisualizer.cs` | Retained point, line, and triangle GPU buffers. |
| `RuntimeWorldRenderer.DebugRenderPhysics` | `XREngine.Runtime.Rendering/Rendering/RuntimeWorldRenderer.cs` | Connects the physics scene publisher to the renderer for one view. |
| `VPRC_RenderDebugPhysics` | `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_RenderDebugPhysics.cs` | Pipeline command that draws the latest frame for one view and one depth mode. |

## Frame Flow

1. Before the simulation step, the physics scene reads the union of view bounds that render views registered with `IncludeDebugRenderViewBounds`. PhysX writes this union into `VisualizationCullingBox`.
2. The scene simulates and fetches results.
3. The scene calls `PublishDebugFrame` one time:
   - PhysX reads the point, line, and triangle counts from the native render buffer. It calls `DebugFrames.BeginWrite`, then `PhysxDebugFrameAdapter.Copy`, then `Publish`.
   - Jolt builds its frame from bodies, contacts, and constraints through `PhysicsDebugGeometryWriter`.
   - When visualization is disabled, the scene publishes an empty frame. It does not read the native buffer.
4. On the render thread, `VPRC_RenderDebugPhysics` calls `DebugRenderPhysics(DepthMode)` on the world.
5. `RuntimeWorldRenderer.DebugRenderPhysics` registers the view frustum bounds for the next step. Then it calls `PhysicsDebugFrameRenderer.Render`.
6. `PhysicsDebugFrameRenderer` acquires the latest frame with a lease. If the generation is new, it uploads the frame to the visualizer for the frame depth mode, and clears the other visualizer. If the generation is already uploaded, it reuses the buffers. Then it draws.

In Edit mode, PhysX does not simulate. `RuntimeWorldRenderer` calls `DebugRenderCollect` at most 30 times per second. PhysX then publishes a preview frame from the native scene shapes through `PublishEditModeDebugFrame`. This path does not advance bodies.

## Ownership And Publication Rules

- The publisher has three slots. The producer writes only to a slot that is not the published slot and that has no readers.
- If all writable slots are pinned, `BeginWrite` returns `null` and increments `DroppedPublications`. The renderer logs this count.
- `Publish` assigns the next generation, fills the telemetry, and then writes the published index with a volatile write. Readers never see a partly written slot.
- `TryAcquireLatest` increments the slot reader count and then confirms that the slot is still published. A reader holds the lease only while it copies or uploads.
- Native PhysX pointers are valid only during the synchronous copy. The adapter does not keep them.
- The generation increases monotonically. The renderer compares it with `UploadedGeneration` to upload at most once per generation, independent of the view count.

## Budgets And Telemetry

- `PhysicsDebugBudget.Default` is 131,072 points, 1,048,576 lines, 524,288 triangles, and 64 MiB.
- Storage arrays grow geometrically (1.5x, from 256 entries) up to the budget. They do not shrink per frame.
- When a budget is full, the writer drops the remaining primitives of that kind. The kept primitives are a deterministic prefix in source order.
- Telemetry records source counts, published counts, dropped counts, published bytes, extraction ticks, and publication ticks.
- The renderer logs a rate-limited warning when a generation drops primitives or when a publication is dropped. A cap never hides output silently.

## Rendering Rules

- `PhysicsDebugFrameRenderer` owns two `InstancedDebugVisualizer` instances: a depth-tested world visualizer and an overlay visualizer. Both use compressed buffers.
- A view draws at most one point batch, one line batch, and one triangle batch per world.
- Triangles stay triangles. The adapter does not expand them into lines.
- `InstancedDebugVisualizer` grows capacity geometrically. It shrinks only after the count stays below one quarter of capacity for 600 consecutive frames.
- When the frame has bounds and the rendering camera frustum does not intersect them, the renderer skips the view and counts it in `CulledViewCount`.
- `VPRC_RenderDebugPhysics` skips light-probe and shadow passes. A `DepthTested` command runs in the opaque forward pass with the scene depth attachment loaded. An `Overlay` command runs in the on-top forward pass.
- Scene fixture geometry must not use the late unlit overlay to get batching. The late overlay targets post-process output without scene depth.
- The physics batch path does not use the individual debug-shape queues, `ConcurrentBag`, `Task.Run`, or blocking task waits.

## Lifetime

`RuntimeWorldRenderer.ResetPhysicsDebugRenderer` disposes the renderer and its GPU buffers when the physics scene is destroyed. It creates a new renderer for the next Edit or Play lifecycle. Native rigid-body actor links are runtime-only and do not enter YAML, MemoryPack, or Play snapshots.

## Diagnostics And Tools

- `XRE_PHYSICS_DEBUG_PRESET` (`XREngineEnvironmentVariables.PhysicsDebugPreset`) is a Unit Testing World launch override. Values: `Disabled`, `Shapes`, `Contacts`, `Joints`, `Simulation`, `All`.
- `Tools/Benchmarks/Measure-PhysicsDebugFrames.ps1` runs the frame benchmarks in `XREngine.UnitTests/Physics/PhysicsDebugFrameBenchmarks.cs`.
- `Tools/Benchmarks/Measure-PhysicsDebugVisualizationMatrix.ps1` runs the live preset matrix.
- `Tools/RenderDoc/Capture-PhysicsDebugVisualization.ps1` captures a RenderDoc frame for each preset.

## Related Documentation

- [Physics Architecture](overview.md)
- [Physics Validation](../../work/testing/physics/physics-validation.md)
