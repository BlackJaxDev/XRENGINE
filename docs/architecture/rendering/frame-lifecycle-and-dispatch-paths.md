# Rendering Frame Lifecycle and Dispatch Paths

[← Rendering Architecture index](README.md)

This document explains how XRENGINE moves renderable state from gameplay code to the renderer, how `CollectVisible -> SwapBuffers -> Render` is staged across threads, and how the engine selects between CPU, GPU, BVH, octree, quadtree, and meshlet-related paths.

It is intended to answer four practical questions:

1. Where does a frame begin and which thread owns each phase?
2. How are worlds, scenes, viewports, and command buffers connected?
3. What collection structures are used today for 2D and 3D visibility?
4. What dispatch paths are real production paths today versus future-facing or partial paths?

## Scope

This document covers the runtime rendering path centered on:

- `XRENGINE/Core/Time/EngineTimer.cs`
- `XRENGINE/Rendering/XRWorldInstance.cs`
- `XRENGINE/Rendering/XRViewport.cs`
- `XRENGINE/Rendering/VisualScene.cs`
- `XRENGINE/Rendering/VisualScene2D.cs`
- `XRENGINE/Rendering/VisualScene3D.cs`
- `XRENGINE/Rendering/Commands/RenderCommandCollection.cs`
- `XRENGINE/Rendering/Commands/GPUScene.cs`
- `XRENGINE/Rendering/Commands/GPURenderPassCollection*.cs`
- `XRENGINE/Rendering/Pipelines/Commands/MeshRendering/*`
- `XRENGINE/Rendering/HybridRenderingManager.cs`

It does not try to fully document every backend detail of OpenGL, Vulkan, OpenXR, or OpenVR. Those topics are covered in the backend-specific architecture pages.

## Mental Model

The engine splits a frame into three rendering-facing phases:

1. `CollectVisible`: decide what the next frame wants to draw.
2. `SwapBuffers`: publish the collected state to the render side.
3. `Render`: consume the published state and submit graphics work.

The important design rule is that gameplay mutation and render submission do not operate on the same buffers at the same time. Update-side data is staged, then explicitly published.

## Main Runtime Actors

| Actor | Responsibility |
|---|---|
| `EngineTimer` | Owns the thread/fence model for `Update`, `PreCollectVisible`, `CollectVisible`, `SwapBuffers`, `RenderFrame`, and `FixedUpdate`. |
| `XRWorldInstance` | Owns the live world binding for rendering. Subscribes world-level callbacks into timer phases. |
| `XRViewport` | Per-camera entry point for `CollectVisible`, `SwapBuffers`, and `Render`. Owns a render pipeline instance. |
| `VisualScene` | Scene-level bridge between world content and renderable proxies. Owns `GPUScene` plus the scene tree. |
| `VisualScene2D` | 2D scene collection using a quadtree. |
| `VisualScene3D` | 3D scene collection using a CPU octree or a GPU command mirror, depending on dispatch mode. |
| `RenderCommandCollection` | Per-viewport command buckets for render passes. Double-buffered between collect and render. |
| `GPUScene` | GPU-resident scene command storage, mesh atlas, material/mesh ID tables, and optional internal command BVH. |
| `GPURenderPassCollection` | GPU culling, optional BVH traversal, SoA extraction, batching, indirect buffer generation, and per-pass diagnostics. |
| `HybridRenderingManager` | Final GPU draw submission path for traditional indirect rendering, with optional meshlet hook. |

## Thread and Fence Model

`EngineTimer` runs the game loop on separate tasks plus the main/render thread.

- `UpdateThread` runs gameplay update continuously.
- `CollectVisibleThread` performs `DispatchCollectVisible()`, waits for the previous render to finish, then runs `DispatchSwapBuffers()`.
- The main/render thread blocks in `WaitToRender()`, waits for swap completion, dispatches `RenderFrame`, then signals render completion.
- `FixedUpdateThread` runs deterministic work such as physics.

The core fence sequence is:

```text
Update thread                 CollectVisible thread                Render thread
-------------                --------------------                -------------
mutate gameplay              DispatchCollectVisible()            wait for _swapDone
enqueue render state         - PreCollectVisible                 DispatchRender()
                             - CollectVisible                    - RenderFrame
                             wait for _renderDone                signal _renderDone
                             DispatchSwapBuffers()
                             signal _swapDone
```

More concretely:

- `CollectVisibleThread` calls `DispatchCollectVisible()` first.
- It then waits on `_renderDone`, meaning the previous render must finish before swap publication begins.
- It runs `Engine.Jobs.ProcessCollectVisibleSwapJobs()` and then `DispatchSwapBuffers()`.
- The render thread waits on `_swapDone`, ensuring it only renders after the new snapshot is published.

This is why `CollectVisible`, `SwapBuffers`, and `Render` should be treated as hot-path rendering phases, not as generic convenience ticks for unrelated work.

## End-to-End Frame Lifecycle

### 1. Update and PostUpdate

Gameplay code mutates transforms, component state, materials, and renderable properties on the update side.

Important consequences:

- Transform changes are not immediately consumed by rendering.
- Render proxies queue changes that will later be applied during swap/publication.
- `XRWorldInstance` uses `PostUpdate` and queued render-matrix publication to keep gameplay mutation separate from render consumption.

At this stage the engine is preparing the next render snapshot, not drawing.

### 2. PreCollectVisible

`EngineTimer.DispatchCollectVisible()` first invokes `PreCollectVisible` synchronously.

For a world instance this currently means:

- `XRWorldInstance.PreCollectVisible()` calls `VisualScene.GlobalCollectVisible()`.

This phase is used for scene-owned housekeeping that must complete before viewports start collecting. In 3D, `VisualScene3D.GlobalCollectVisible()` flushes pending renderable add/remove operations. In CPU-dispatch mode it also swaps the octree state needed for subsequent tree walks.

### 3. CollectVisible

After `PreCollectVisible`, `EngineTimer.DispatchCollectVisible()` invokes `CollectVisible` asynchronously across subscribers.

Two major subscriber categories matter here:

- World-level collection such as `XRWorldInstance.GlobalCollectVisible()`, which currently collects light visibility data.
- Viewport-level collection such as `XRViewport.CollectVisibleAutomatic()`, which drives camera-based collection into that viewport's `RenderCommandCollection`.

For a normal 3D viewport, the flow is:

```text
XRViewport.CollectVisible()
  -> resolve camera and world
  -> world.VisualScene.CollectRenderedItems(...)
  -> optional screen-space UI collect
```

The viewport does not decide the spatial structure itself. It delegates that to the scene.

### 4. SwapBuffers

After collection finishes and after the render thread signals that the previous frame is done, the collect-visible thread runs swap work.

There are two distinct swap layers:

1. World/scene swap
2. Viewport command-buffer swap

#### World/scene swap

`XRWorldInstance.GlobalSwapBuffers()` performs world-level render publication:

- Applies queued render matrices.
- Processes pending render-matrix updates for meshes.
- Calls `VisualScene.GlobalSwapBuffers()`.
- Swaps lights.
- Finalizes render-side stats.

`VisualScene.GlobalSwapBuffers()` performs the scene-level generic swap:

- `GenericRenderTree.Swap()`
- `GPUCommands.SwapCommandBuffers()`

That means scene tree state and GPU scene command buffers are both explicitly published before render.

#### Viewport command-buffer swap

Separately, each viewport swaps its own render-pass command buckets:

```text
XRViewport.SwapBuffers()
  -> RenderCommandCollection.SwapBuffers()
  -> optional screen-space UI swap
```

This is the handoff from command collection to command consumption for that viewport's pipeline.

### 5. Render

Once `_swapDone` is signaled, the render thread dispatches `RenderFrame`.

At the window level the relevant order is:

```text
XRWindow.RenderFrame()
  -> Window.DoEvents()
  -> Window.DoRender()
  -> XRWindow.RenderCallback()
       -> TargetWorldInstance.GlobalPreRender()
       -> RenderViewportsCallback
       -> render each viewport / pipeline
       -> TargetWorldInstance.GlobalPostRender()
       -> Renderer.RenderWindow(delta)
       -> PostRenderViewportsCallback
```

`GlobalPreRender` and `GlobalPostRender` are render-thread hooks around actual viewport rendering. In 3D they are also where GPU BVH raycast dispatch/completion hooks run.

## Ownership by Layer

The frame is easiest to reason about if you separate ownership by layer:

### Engine layer

- Owns thread cadence and fences.
- Does not decide octree vs BVH vs CPU vs GPU draw policy itself.

### World layer

- Owns global scene publication and global render hooks.
- Owns transform publication and light visibility state.

### Viewport layer

- Owns camera-specific collection and per-viewport command buffers.
- Owns render pipeline execution.

### Scene layer

- Owns the spatial structure used to turn world renderables into command submissions.
- Owns the GPU scene mirror and scene-level acceleration structures.

### Pipeline/pass layer

- Owns pass-specific draw routing.
- Chooses CPU draw submission vs GPU-driven draw submission per pass.

## Collection Structures Used Today

The engine uses more than one acceleration structure, and they operate at different scopes.

### Quadtree: 2D scene collection

`VisualScene2D` uses a `Quadtree<RenderInfo2D>`.

Use cases:

- 2D scene/UI-style renderables
- Orthographic bounds-based collection
- Screen-space UI collection helpers

Current behavior:

- `CollectRenderedItems()` flushes pending operations and swaps the quadtree before walking it.
- Collection uses either `CollectAll()` or `CollectVisible()` depending on whether a collection volume is present.

### Octree: CPU 3D scene collection

`VisualScene3D` uses an `Octree<RenderInfo3D>` as its CPU scene visibility structure.

This is the active 3D collection structure when GPU render dispatch is not enabled.

CPU 3D collection path:

```text
XRViewport.CollectVisible()
  -> VisualScene3D.CollectRenderedItems(...)
       -> RenderTree.CollectVisible(...)
            -> RenderInfo3D.AllowRender(...)
            -> renderable.CollectCommands(...)
```

Important note:

- This is the structure that answers the practical question "what are we culling 3D renderables with on the CPU today?" The answer is still the octree.

### GPUScene command mirror: GPU-driven collection handoff

When GPU render dispatch is active, `VisualScene3D` does not walk the octree to populate viewport command buffers. Instead, scene renderables are mirrored into `GPUScene`, and the later GPU pass performs the real culling work.

Current 3D GPU-dispatch collect path:

```text
XRViewport.CollectVisible()
  -> VisualScene3D.CollectRenderedItems(...)
       -> CollectRenderedItemsGpu(...)
            -> iterate tracked renderables
            -> AllowRender(...)
            -> CollectCommands(...)
```

That method is currently a linear snapshot/filter over tracked renderables. The actual per-command visibility reduction then occurs inside the GPU pass pipeline, not in `VisualScene3D` itself.

So there are two distinct collection layers in GPU mode:

1. CPU-side command emission into the viewport's command collection.
2. GPU-side culling/sorting of the mirrored `GPUScene` commands before draw submission.

### Internal GPU BVH: scene command acceleration for GPU culling

`GPUScene` can act as an `IGpuBvhProvider` and build an internal BVH over command bounds using `GpuBvhTree`.

What it is:

- A scene-level GPU BVH built from command AABBs/bounds.
- Used by GPU culling passes, not by the CPU octree collection path.

What it is not:

- It is not a CPU replacement for `VisualScene3D.RenderTree`.
- It is not the same thing as per-mesh triangle BVHs used for picking/raycasting.

Current behavior:

- `VisualScene3D` propagates `UseGpuBvh` into `GPUCommands.UseGpuBvh` and `GPUCommands.UseInternalBvh`.
- `GPUScene.PrepareBvhForCulling()` rebuilds or refits the internal command BVH as needed.
- `GPURenderPassCollection` chooses BVH culling only if `scene.UseGpuBvh` is enabled and the provider is ready.

### CPU mesh BVH: per-mesh triangle acceleration

There is also a separate BVH family used for per-mesh triangle work.

Examples:

- `XRMesh.BVHTree`
- `RenderableMesh.GetSkinnedBvh()`
- `SkinnedMeshBvhScheduler`
- world raycast helpers in `XRWorldInstance`

This BVH is for:

- raycasts and picking
- static mesh triangle traversal
- skinned mesh triangle BVH rebuild/refit

It is not the scene-wide 3D renderable visibility structure.

## Dispatch Types Used Today

There are two separate axes to keep straight:

1. Scene collection path
2. Draw submission path

They are related, but they are not identical.

### A. Scene collection path

#### CPU scene collection

- 2D: quadtree
- 3D: octree

This is the classic scene-tree path.

#### GPU-oriented scene collection

- Scene renderables are mirrored into `GPUScene`.
- `VisualScene3D` still does CPU-side filtering for command emission, but final visibility reduction is deferred to GPU culling.

### B. Draw submission path

#### CPU traditional draw path

The traditional CPU draw path is selected when the mesh pass command's `GPUDispatch` flag is false.

This path:

- walks the CPU-side render command lists for a render pass
- submits draws through the CPU/traditional renderer path

This is the simplest and most direct path, and it does not rely on GPU-driven culling/indirect generation.

#### GPU traditional indirect draw path

This is the main GPU-driven production path today.

It consists of:

1. `GPUScene` owns GPU-resident commands, mesh atlas data, material IDs, mesh IDs, and optional BVH.
2. `GPURenderPassCollection` performs per-pass culling, optional BVH traversal, optional SoA extraction, batching, and indirect buffer building.
3. `HybridRenderingManager` submits the resulting indirect draw buffers.

Within this path, the GPU culling sub-modes are:

- passthrough culling: diagnostics/special cases
- frustum culling: default GPU frustum test path
- BVH culling: hierarchical GPU BVH traversal when enabled and ready

Optional data/processing variants inside the GPU path include:

- AoS command layout
- extracted SoA culling layout
- occlusion passes and Hi-Z support
- GPU-driven batching and instancing

Fallback policy when GPU dispatch is enabled:

- mesh commands marked `ExcludeFromGpuIndirect` are not silently rendered on the CPU anymore during GPU mesh passes; the pass now warns and skips them so hidden per-submesh CPU reversion is visible
- the old full-pass CPU mesh safety-net is now treated as an explicit diagnostics-only path; it only runs when GPU CPU fallback diagnostics are enabled and it emits a warning when triggered
- GPU culling-stage CPU recovery remains a separate diagnostics path controlled by the existing fallback/debug settings and profile policy

#### Meshlet draw path intent

The codebase has a path split for `Traditional` versus `Meshlet` mesh rendering intent.

However, the dedicated meshlet render command path is not yet implemented as a production path:

- `VPRC_RenderMeshesPassShared` can route to `Traditional` or `Meshlet` intent.
- `VPRC_RenderMeshesPassMeshlet` currently logs a warning and falls back to `Traditional`.

There is real meshlet infrastructure in the repository:

- `MeshletGenerator`
- `MeshletCollection`
- task/mesh shader assets
- `HybridRenderingManager.UseMeshletPipeline`

But the pass-router-level meshlet path should currently be treated as experimental/incomplete rather than as the primary shipping draw path.

## How GPU Culling Chooses Its Sub-Path

Inside `GPURenderPassCollection`, per-pass GPU culling selection is roughly:

```text
if diagnostics demand passthrough:
    PassthroughCull
else if GPU BVH is enabled and ready:
    BvhCull
else:
    FrustumCull
```

That means "GPU dispatch enabled" does not automatically mean "BVH enabled." BVH is a second toggle layered on top of GPU dispatch.

## Settings and Policy That Influence Path Selection

### `GPURenderDispatch`

This is the primary toggle for whether mesh passes attempt the GPU-driven path.

Effects:

- changes `VisualScene3D` behavior between CPU octree collection and GPU-scene-oriented collection
- changes mesh pass execution between CPU traditional and GPU-driven traditional indirect
- is propagated into render pipelines by rendering settings helpers
- suppresses silent per-submesh CPU draw fallback in GPU mesh passes; skipped opt-out meshes are warned instead

### `UseGpuBvh`

This is a narrower toggle than `GPURenderDispatch`.

Effects:

- enables the internal `GPUScene` BVH path when supported
- allows `GPURenderPassCollection` to prefer `BvhCull()` over plain frustum culling
- also enables GPU BVH raycast helpers and related profiling/timing paths

Default state today:

- `UseGpuBvh` defaults to `false`

### Vulkan feature-profile resolution

Requested settings are not the final word. Rendering settings flow through profile/policy resolution.

For example:

- `Engine.Rendering.ResolveGpuRenderDispatchPreference(...)`
- Vulkan feature profile rules
- debug/diagnostic profiles that allow or suppress CPU safety nets

So when documenting a path you should distinguish between:

- requested setting
- effective runtime path

Also distinguish between:

- explicit diagnostics fallback that was requested by settings
- fallback that was merely available historically but is now suppressed with a warning when GPU dispatch is meant to be authoritative

## CollectVisible, SwapBuffers, and Render by Responsibility

This is the shortest useful operational summary.

### CollectVisible

Purpose:

- decide what the next frame wants to draw
- update per-viewport pass buckets
- gather UI and camera-scoped visibility

World responsibilities:

- scene pending-operation flush
- light visibility bookkeeping

Viewport responsibilities:

- camera/frustum/collection volume resolution
- command generation per render pass

### SwapBuffers

Purpose:

- publish update-side state to render-side buffers
- make tree, scene, transform, and viewport command snapshots safe for render consumption

World responsibilities:

- apply render matrices
- swap scene and light state

Viewport responsibilities:

- swap `RenderCommandCollection`
- swap screen-space UI command buffers

### Render

Purpose:

- consume the published snapshot only
- execute render-pipeline passes
- submit graphics backend work

World responsibilities:

- pre/post render hooks
- shadow-map and shared resource prep

Viewport responsibilities:

- execute the command chain
- route mesh passes to CPU or GPU dispatch
- submit final draws

## Current-State Summary

If you need the practical, non-aspirational summary of the engine as it exists today:

- 2D scene visibility uses a quadtree.
- 3D CPU scene visibility uses an octree.
- GPU-driven rendering uses `GPUScene` plus `GPURenderPassCollection` for later GPU culling and indirect generation.
- GPU BVH exists and is wired for GPU command culling, but it is optional and not the same thing as replacing the CPU octree scene tree.
- Per-mesh CPU BVHs exist for picking/raycast/skinned-mesh work.
- Meshlet infrastructure exists, but the explicit meshlet mesh-pass router currently falls back to the traditional path.

## Source Code Guide

Use this list when tracing the system in code.

### Frame lifecycle

- `XRENGINE/Core/Time/EngineTimer.cs`
- `XRENGINE/Rendering/API/XRWindow.cs`

### World and viewport orchestration

- `XRENGINE/Rendering/XRWorldInstance.cs`
- `XRENGINE/Rendering/XRViewport.cs`

### Scene collection

- `XRENGINE/Rendering/VisualScene.cs`
- `XRENGINE/Rendering/VisualScene2D.cs`
- `XRENGINE/Rendering/VisualScene3D.cs`

### Command buffering and GPU scene state

- `XRENGINE/Rendering/Commands/RenderCommandCollection.cs`
- `XRENGINE/Rendering/Commands/GPUScene.cs`

### GPU culling and indirect generation

- `XRENGINE/Rendering/Commands/GPURenderPassCollection.Core.cs`
- `XRENGINE/Rendering/Commands/GPURenderPassCollection.CullingAndSoA.cs`
- `XRENGINE/Rendering/Commands/GPURenderPassCollection.IndirectAndMaterials.cs`
- `XRENGINE/Rendering/Compute/GpuBvhTree.cs`

### Draw routing

- `XRENGINE/Rendering/Pipelines/Commands/MeshRendering/Shared/VPRC_RenderMeshesPassShared.cs`
- `XRENGINE/Rendering/Pipelines/Commands/MeshRendering/Traditional/VPRC_RenderMeshesPassTraditional.cs`
- `XRENGINE/Rendering/Pipelines/Commands/MeshRendering/Meshlet/VPRC_RenderMeshesPassMeshlet.cs`
- `XRENGINE/Rendering/HybridRenderingManager.cs`

### Per-mesh BVH and raycast helpers

- `XRENGINE/Rendering/Compute/SkinnedMeshBvhScheduler.cs`
- `XRENGINE/Rendering/XRWorldInstance.cs`
- `XREngine.Data/Trees/BVH/BVH.cs`

## Related Documents

- [Rendering Architecture](../../api/rendering.md)
- [Rendering Architecture index](README.md)
- [Rendering Code Map](RenderingCodeMap.md)
- [OpenGL Renderer](opengl-renderer.md)
- [Vulkan Renderer](vulkan-renderer.md)
- [OpenXR VR Rendering](openxr-vr-rendering.md)
- [OpenVR (SteamVR) Rendering](openvr-rendering.md)