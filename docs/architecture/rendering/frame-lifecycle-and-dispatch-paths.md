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
| `VisualScene3D` | 3D scene collection using a CPU BVH/octree or a GPU command mirror, depending on dispatch mode. |
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

The default late-frame policy is `BlockUntilFresh`, which preserves that strict
fresh-snapshot fence. `XRE_COLLECT_VISIBLE_LATE_POLICY=ReusePreviousVisibility`
is a diagnostic policy for isolating render-thread pressure from visibility
collection: once at least one real collect/swap has completed, the render thread
may render the previously published visibility snapshot instead of blocking on a
late collect/swap. Accepted short aliases are `block`, `fresh`, `reuse`, and
`stale`. This policy does not add another buffer; it only decides whether a
missed collect/swap fence blocks render or records a stale-snapshot reuse.

This is why `CollectVisible`, `SwapBuffers`, and `Render` should be treated as hot-path rendering phases, not as generic convenience ticks for unrelated work.

## Window, Render, And Input Ownership

Window ownership is now explicit:

- The app/update thread owns gameplay and editor decisions.
- The window thread owns native Silk.NET window/event/input callbacks.
- The render thread owns renderer state, graphics context work, swapchain/present
  work, GPU resource wrapper creation, and renderer-specific readback setup.

Code outside backend-owned paths should not reach through `XRWindow.Window` or
`XRWindow.Input`. `XRWindow` publishes app/editor-safe wrappers for common
window events and immutable snapshots for window surface, close/focus, and
input state. The local player viewport contract exposes `WindowInputSnapshot`;
gameplay/editor possession binds snapshot-backed keyboard and mouse adapters
instead of Silk input devices. Cursor capture requests flow back through the
viewport/window wrapper instead of mutating the live Silk mouse object.

Use `IRuntimeRenderingHostServices.EnqueueWindowThreadTask` or
`InvokeWindowThreadTask<T>` for native window operations that require the window
owner. Use `EnqueueRenderThreadTask` or `InvokeRenderThreadTask<T>` for
GPU-affine work that needs a result, such as editor preview texture handles or
readback helper setup.

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

This phase is used for scene-owned housekeeping that must complete before viewports start collecting. In 3D, `VisualScene3D.GlobalCollectVisible()` flushes pending renderable add/remove operations. In CPU-dispatch mode it also swaps the active CPU spatial tree (`Octree` by default, or `Bvh` when selected) needed for subsequent tree walks.

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

Profiler scopes split this handoff into three different costs:

- `EngineTimer.CollectVisibleThread.WaitForRender`: downstream render pressure; the collect-visible thread is asleep waiting for render to finish.
- `EngineTimer.CollectVisibleThread.ProcessCollectVisibleSwapJobs`: queued collect/swap jobs that must run before buffer publication.
- `EngineTimer.CollectVisibleThread.DispatchSwapBuffers`: the actual world, scene, viewport, and command-buffer publication callbacks.

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

If `ReusePreviousVisibility` is active and `_swapDone` is late, the render
thread may dispatch with the last published visibility snapshot instead of
waiting. The frame lifecycle telemetry then records
`render_wait_reason=ReusingPreviousVisibility`,
`skipped_collect_frames`, and `stale_collect_reuse_frames` for that sample.

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
       -> ActiveCpuRenderTree.CollectVisible(...)
            -> RenderInfo3D.AllowRender(...)
            -> renderable.CollectCommands(...)
```

Important note:

- This is the structure that answers the practical question "what are we culling 3D renderables with on the CPU today?" `CpuDirect` defaults to the CPU BVH. The `CpuSceneCullingStructure` setting and `XRE_CPU_SCENE_CULLING_STRUCTURE` remain diagnostic overrides for comparing the legacy octree.
- The profiler's render stats now report CPU spatial tree mode, collect time, total nodes/items, root-held items, max items per node, max depth, and unbounded items. High octree root-held counts are the signal for origin-crossing bounds piling up in the main octree region.

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
- Used by GPU culling passes, not by the CPU scene collection path.

What it is not:

- It is not a CPU replacement for `VisualScene3D.RenderTree`.
- It is not the same thing as per-mesh triangle BVHs used for picking/raycasting.

Current behavior:

- `VisualScene3D` derives `GPUCommands.UseGpuBvh` and `GPUCommands.UseInternalBvh` from the effective `EMeshSubmissionStrategy`.
- `GPUScene.PrepareBvhForCulling()` rebuilds or refits the internal command BVH as needed.
- All GPU instrumented, GPU zero-readback, and GPU meshlet strategies request BVH culling. `GPURenderPassCollection` uses the flat GPU frustum path only when the BVH shader or provider resources are not ready.

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

The traditional CPU draw path is selected when the mesh pass command's `MeshSubmissionStrategy` is `CpuDirect`. The older `GPUDispatch == false` path still maps to this strategy.

This path:

- walks the CPU-side render command lists for a render pass
- submits draws through the CPU/traditional renderer path

This is the simplest and most direct path, and it does not rely on GPU-driven culling/indirect generation.

#### GPU traditional indirect draw path

This is the main GPU-driven production path today. It has two explicit strategy modes:

- `GpuIndirectInstrumented`: validation and bring-up path; CPU readbacks, count dumps, and CPU safety-net fallback are allowed only here.
- `GpuIndirectZeroReadback`: production path; GPU-written count and material-tier buffers are consumed directly and steady-state CPU readbacks are forbidden.

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

Fallback policy when GPU mesh submission is enabled:

- mesh commands marked `ExcludeFromGpuIndirect` are not silently rendered on the CPU anymore during GPU mesh passes; the pass now warns and skips them so hidden per-submesh CPU reversion is visible
- the old full-pass CPU mesh safety-net is an explicit diagnostics-only path; it only runs for `GpuIndirectInstrumented` when GPU CPU fallback diagnostics are enabled and it emits a warning when triggered
- the zero-readback path must not call CPU count, batch, per-view draw count, or indirect command dump readbacks
- GPU culling-stage CPU recovery remains a separate diagnostics path controlled by the existing fallback/debug settings and profile policy

#### Meshlet draw path intent

The codebase has a path split for `Traditional` versus `Meshlet` mesh rendering intent.

The dedicated meshlet render command path is still experimental:

- `VPRC_RenderMeshesPassShared` can route to `Traditional` or `Meshlet` intent.
- `GpuMeshletZeroReadback` requires production `SupportsMeshletDispatch()` from the active renderer, not just visible mesh shader extensions. `GpuMeshletInstrumented` uses the same capability gate and is selected only for explicit diagnostics.
- `MeshShaderDialect`, `SupportsDirectMeshTaskDispatch()`, and `SupportsIndirectCountMeshTaskDispatch()` expose partial backend support for diagnostics.
- `RenderGPU(pass, strategy)` sets `UseMeshletPipeline` for the duration of the call whenever `strategy` is `GpuMeshlet*`, so direct side-pass callers cannot request meshlets while silently entering the traditional indirect path.
- unsupported production meshlet dispatch falls back at resolver/pass-router level to `GpuIndirectZeroReadback` when available, or to the profile-approved non-meshlet fallback. Once a selected meshlet strategy enters the render manager, any meshlet dispatch failure is logged and skipped rather than drawing traditional meshes.

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

That means the submission strategy selects the scene hierarchy: `CpuDirect` uses the CPU hierarchy, while GPU-driven strategies use the GPU command BVH when available.

## Settings and Policy That Influence Path Selection

### `EMeshSubmissionStrategy`

This is the effective strategy for mesh pass submission. It is resolved by `Engine.Rendering.ResolveMeshSubmissionStrategy()` from profile, settings, and renderer capability probes.

Effects:

- changes `VisualScene3D` behavior between CPU scene collection and GPU-scene-oriented collection
- changes mesh pass execution between CPU traditional, instrumented GPU indirect, zero-readback GPU indirect, and meshlet submission
- is propagated into render pipelines by rendering settings helpers
- suppresses silent per-submesh CPU draw fallback in GPU mesh passes; skipped opt-out meshes are warned instead

`GPURenderDispatch` remains as a compatibility input. Boolean call sites that pass `true` preserve the legacy instrumented GPU indirect behavior.

### Strategy-driven scene hierarchy

There is no independent `UseGpuBvh` setting or Vulkan GPU-BVH environment gate. The effective mesh submission strategy owns the choice:

- `CpuDirect` uses the CPU scene hierarchy, whose default is `CpuBvhRenderTree`.
- `GpuIndirectInstrumented`, `GpuIndirectZeroReadback`, and both GPU meshlet strategies request the internal `GPUScene` BVH.
- Missing or not-yet-ready BVH shaders/buffers cause a visible GPU flat-frustum fallback; they do not switch submission back to the CPU.

### Vulkan feature-profile resolution

Requested settings are not the final word. Rendering settings flow through profile/policy resolution.

For example:

- `Engine.Rendering.ResolveMeshSubmissionStrategy(...)`
- Vulkan feature profile rules
- debug/diagnostic profiles that allow or suppress CPU safety nets

So when documenting a path you should distinguish between:

- requested setting
- effective runtime path

Also distinguish between:

- explicit diagnostics fallback that was requested by settings
- fallback that was merely available historically but is now suppressed with a warning when GPU dispatch is meant to be authoritative

### Collect-visible late policy

`EngineTimer.CollectVisibleLatePolicy` controls what the render thread does
when collect/swap publication is late:

| Policy | Behavior |
|---|---|
| `BlockUntilFresh` | Default. Render waits on `_swapDone` so every render consumes the newest published collect/swap snapshot. |
| `ReusePreviousVisibility` | Diagnostic mode. After at least one collect/swap has completed, render may reuse the previous visibility snapshot rather than blocking on a late collect/swap. |

The process environment override is `XRE_COLLECT_VISIBLE_LATE_POLICY`. Use
`BlockUntilFresh` for correctness baselines and `ReusePreviousVisibility` only
when measuring whether visible-collection work or render-thread pressure is the
limiting factor. Profile captures and the live profiler report the effective
policy, frame lifecycle ids, wait durations, wait reasons, skipped collect
frames, and stale reuse counts.

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
- route mesh passes to the resolved mesh submission strategy
- submit final draws

Backend command-buffer work happens after the viewport/pipeline has emitted its render commands. In Vulkan command-chain mode, the render thread still owns the authoritative ordering and submit path:

```text
Render thread
  -> drain published Vulkan FrameOps
  -> sort them through the render graph compiler
  -> freeze the Vulkan resource-plan revision
  -> lower ops into visibility/render packets and chain groups
  -> record or reuse secondary command chains
  -> record or reuse the primary command buffer
  -> submit on the graphics queue
```

The worker split is deliberately below scene mutation and below `SwapBuffers`. Workers receive only immutable packet/schedule data plus frozen Vulkan resource/descriptor snapshots. They must not mutate scene state, render pipeline state, descriptor pools, FBO declarations, or the active resource planner. Dynamic UI/text/profiler overlay ops are kept as volatile chains so they can be re-recorded without dirtying static scene chains.

When parallel packet build is enabled, independent packet snapshots can be built off-thread and then sorted deterministically before scheduling. When command-chain worker recording is enabled, worker timing is tracked separately from render-thread wait time. Primary command-buffer reuse remains a render-thread decision because it depends on the final ordered group signature and secondary command-buffer handles.

## Current-State Summary

If you need the practical, non-aspirational summary of the engine as it exists today:

- 2D scene visibility uses a quadtree.
- 3D CPU scene visibility uses a CPU BVH by default, with an opt-in octree via engine setting, project override, or `XRE_CPU_SCENE_CULLING_STRUCTURE=Octree`.
- GPU-driven rendering uses `GPUScene` plus `GPURenderPassCollection` for later GPU culling and indirect generation.
- GPU BVH exists and is wired for GPU command culling, but it is optional and not the same thing as choosing the CPU BVH/octree scene tree.
- Vulkan command-chain mode is a feature-flagged backend recording path. It consumes the same sorted `FrameOp` stream, caches reusable secondary chains per swapchain image, isolates volatile overlay work, and keeps the legacy frame-op recorder available for fallback and validation.
- Per-mesh CPU BVHs exist for picking/raycast/skinned-mesh work.
- Meshlet infrastructure exists, with `GpuMeshletZeroReadback` as the production zero-readback strategy and `GpuMeshletInstrumented` as the diagnostics strategy. Both require production indirect-count mesh task dispatch; unsupported requests fall back visibly through the mesh submission strategy resolver.

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

- [Rendering Architecture](../../user-guide/rendering.md)
- [Rendering Architecture index](README.md)
- [Rendering Code Map](code-map.md)
- [OpenGL Renderer](opengl-renderer.md)
- [Vulkan Renderer](vulkan-renderer.md)
- [OpenXR VR Rendering](openxr-vr-rendering.md)
- [OpenVR (SteamVR) Rendering](openvr-rendering.md)
