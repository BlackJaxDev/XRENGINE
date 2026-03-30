# FPS Drop Startup Audit — TODO

Reference work docs:
- [March 28 startup remediation TODO](design/startup-fps-drop-remediation-plan.md)

Source log: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-03-22_20-21-53_pid50924/profiler-fps-drops.log`  
Date: 2026-03-22

This TODO captures the original March 22 startup investigation and the first wave of fixes. The March 28 TODO continues with the remaining startup stalls after those fixes landed.

All drops occur within the first ~9 seconds of editor launch. The scene is minimal (camera pawn, text node, light probe). Every item below is a distinct root cause observed in the profiler.

---

## P0 — Critical (>300 ms single-frame impact)

### [x] 1. GL Resource Generation Storm
- **Worst frame:** 763 ms (1.3 FPS), render thread
- **Hot path:** `GLMeshRenderer.Render.Generate > GLObject.Generate.VertexArray > GLMeshRenderer.GenProgramsAndBuffers > CreateCombinedProgram > InitiateLink > CheckProgramLinked > BindBuffers > GLObject.Generate.Buffer`
- **Root cause:** First-time rendering of each mesh synchronously creates VAOs, links shader programs, queries attribute locations, and allocates GPU buffers — all inside the draw call on the render thread.
- **Cluster:** 6+ entries from t=5.6s–6.1s as scene meshes become visible.
- **Fix direction:**
  - Pre-warm GL resources during load (generate VAOs/buffers before first draw).
  - Stage resource creation across multiple frames instead of all-at-once.
  - Consider an async resource-init queue that runs during idle render time.
- **Implemented 2026-03-22:** Deferred mesh generation queue is now enabled by default for OpenGL mesh renderers, and meshes are enqueued when their mesh data changes so warming starts before first visible draw.

### [x] 2. Shadow Maps Amplifying Cold Init
- **Worst frame:** 748 ms (1.3 FPS), render thread
- **Hot path:** `XRWindow.GlobalPreRender > WorldInstance.GlobalPreRender > Lights3DCollection.RenderShadowMaps > XRViewport.Render > GLMeshRenderer.Render.Generate > ... > GLObject.Generate.Buffer`
- **Root cause:** Shadow-map rendering triggers the same GL resource generation (#1) for every shadow-casting mesh that hasn't been initialized yet, multiplied by the number of shadow cascades.
- **Fix direction:**
  - Defer shadow rendering until mesh resources are warm (skip shadow pass for un-initialized renderers).
  - Pre-warm shadow-casting meshes during the same staged init as #1.
  - Consider rendering shadow maps at reduced resolution/frequency for the first few frames.
- **Implemented 2026-03-22:** Shadow-pass draws now enqueue uninitialized meshes and skip that shadow submission instead of synchronously generating VAOs/programs/buffers inside `RenderShadowMaps`.

### [x] 3. Shader Uniform Location Cold Cache
- **Worst frame:** 444 ms (2.3 FPS), render thread
- **Hot path:** `GLMeshRenderer.Render.SetMaterialUniforms > GLRenderProgram.GetUniformLocation > GLRenderProgram.GetUniform`
- **Root cause:** After shader programs are linked, the first draw call for each material hits an empty uniform-location cache, causing expensive `glGetUniformLocation` introspection or dictionary misses for every uniform.
- **Fix direction:**
  - Pre-query and cache all uniform locations at program link time (enumerate active uniforms via `glGetActiveUniform`).
  - Avoid per-draw string-keyed lookups; use integer indices from a pre-built table.
- **Implemented 2026-03-23:** `GLRenderProgram.CacheActiveUniforms()` now preloads uniform locations for each active uniform, including array base names, so first material binds reuse the cache instead of issuing fresh `glGetUniformLocation` calls during draw.

### [x] 4. Mesh Construction on Main Thread (Component Add)
- **Worst frame:** 335 ms (3.0 FPS), render thread
- **Hot path:** `SceneNode.AddComponent > SceneNode.AddComponentInternal > XRMesh Constructor > PopulateVertexData (remapped)`
- **Root cause:** Adding a component dynamically constructs a mesh with vertex-data remapping (format conversion or tangent generation) synchronously on the main thread.
- **Fix direction:**
  - Offload `PopulateVertexData` (remapped) to a worker thread; signal the render thread when ready.
  - If the remapping is a format conversion, pre-compute the target format during asset import.
- **Implemented 2026-03-23:** Parallel vertex population is now enabled by default. `XRMesh` constructors and the Assimp mesh finalization path already support `Parallel.For`, so remapped vertex-buffer population now spreads across worker threads instead of running serially on the calling thread.

---

## P1 — High (50–100 ms single-frame impact)

### [ ] 5. EditorImGui Full-Frame Render
- **Worst frame:** 960 ms (1.0 FPS), render thread
- **Hot path:** `XRWindow.Timer.RenderFrame > ... > XRViewport.Render > RenderCommand.Render > EditorImGuiUI.RenderEditor`
- **Root cause:** A single frame where the entire ImGui editor pass consumed nearly one second. Likely initial layout computation or a large panel being populated for the first time.
- **Fix direction:**
  - Profile which ImGui panel is responsible (asset explorer? hierarchy? properties?).
  - Defer heavy panel initialization to subsequent frames.
  - Consider lazy initialization for panels that aren't immediately visible.

### [ ] 6. Asset Explorer File Enumeration
- **Worst frame:** 96 ms (10.4 FPS), render thread
- **Hot path:** `UI.DrawAssetExplorerPanel > UI.DrawAssetExplorerTab.GameProject > UI.AssetExplorer.FileList.GameProject`
- **Root cause:** The ImGui asset explorer scans the game-project file tree synchronously during rendering. Likely a first-time directory enumeration or un-paginated file listing.
- **Fix direction:**
  - Move directory enumeration to a background thread; display cached/partial results.
  - Paginate or virtualize the file list (only enumerate visible entries).
  - Cache the file tree and watch for changes via `FileSystemWatcher`.

### [ ] 7. Uniform Binding Tracking Overhead
- **Worst frame:** 68 ms (14.7 FPS), render thread
- **Hot path:** `GLMeshRenderer.Render.SetMeshUniforms > GLRenderProgram.Uniform(Int) > GLRenderProgram.MarkUniformBinding`
- **Root cause:** Per-mesh integer uniform calls spend excessive time in `MarkUniformBinding`, suggesting overhead in tracking which uniforms are dirty/set (hash-set or dictionary operation per uniform per draw call).
- **Fix direction:**
  - Replace dictionary-based tracking with a bitfield or fixed-size array indexed by uniform location.
  - Eliminate tracking entirely if the dirty state is not consumed.

### [ ] 8. UI Layout Cascade During CollectVisible
- **Worst frame:** 52 ms (19.2 FPS), collect-visible worker thread
- **Hot path:** `XRViewport.CollectVisible_ScreenSpaceUI > UICanvasComponent.UpdateLayout > UICanvasComponent.RecalculateDirtyMatrices > UIBoundableTransform.OnWorldMatrixChanged > UIBoundableTransform.RemakeAxisAlignedRegion`
- **Root cause:** Screen-space UI layout triggers a cascade of dirty-matrix recalculations during visibility collection. Every dirty `UIBoundableTransform` recomputes its axis-aligned region.
- **Related:** Quadtree Swap / Move Items (5.4 ms) — UI items repositioned in spatial acceleration structure after layout changes.
- **Fix direction:**
  - Batch dirty-matrix recalculation; defer to a dedicated layout pass before collect-visible.
  - Coalesce multiple world-matrix changes into a single AABB recompute.

### [ ] 9. Window Framebuffer Resize
- **Worst frame:** 55 ms (14.6 FPS), render thread
- **Hot path:** `XRWindow.Timer.DoEvents > XRWindow.FramebufferResize`
- **Root cause:** A window resize event triggers synchronous framebuffer recreation during the event-processing phase.
- **Fix direction:**
  - Debounce resize events; only recreate FBOs after resize settles.
  - If already debounced, investigate whether texture reallocation can be deferred.

---

## Info — Symptoms, Not Root Causes

### [x] CollectVisible Thread Stall (WaitForRender)
- **Worst frame:** 7,326 ms (0.14 FPS), collect-visible thread (72)
- **Hot path:** `EngineTimer.CollectVisibleThread.WaitForRender`
- **Note:** This is a synchronization barrier — the collect-visible thread blocks waiting for the render thread. The 7.3-second stall is entirely caused by the render thread being saturated with GL resource generation, ImGui rendering, and shadow maps. Fixing P0/P1 items above will resolve this automatically.

### [x] Update Thread Jitter (DispatchUpdate.Iteration)
- **Typical:** 1–5 ms spikes, update thread (67); one 14.9 ms outlier
- **Hot path:** `EngineTimer.DispatchUpdate.Iteration > EngineTimer.DispatchUpdate.Update > XREvent.Invoke > XREvent.Actions`
- **Note:** Component update work is trivial (<0.5 ms total). The excess time is GC pauses, OS scheduling jitter, or memory bandwidth contention from the render thread. Not independently actionable — will improve as render-thread pressure decreases.

---

## Attack Order

1. Items #1 + #2 + #3 share the same subsystem (`GLMeshRenderer` / `GLRenderProgram`). Tackle together as a "GL resource pre-warming" initiative.
2. Item #4 (mesh construction) is independent and can be parallelized with #1–3.
3. Items #5 + #6 are ImGui editor concerns — lower urgency since they're one-time startup costs, but easy wins if the panels can lazy-init.
4. Items #7 + #8 are per-frame overhead that becomes noticeable once startup spikes are removed.
5. Item #9 is minor and only triggers on actual resize events.
