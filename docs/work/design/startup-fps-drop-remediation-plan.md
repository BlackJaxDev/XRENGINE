# Startup FPS Drop Remediation — TODO

Source log: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-03-28_23-42-46_pid33644/profiler-fps-drops.log`  
Date: 2026-03-28  
Predecessor: [March 22 startup audit](../fps-drop-startup-todo.md) (items 1–4 implemented)

206 FPS-drop entries across ~8.5 s of startup. All items below are residual stalls that survived the March 22 fixes (deferred mesh queue, shadow-skip, uniform prewarming, parallel vertex population).

---

## P0 — Critical (>400 ms single-frame impact)

### [x] 1. Late CPU Mesh / Component Construction — FIXED

- **Worst frame:** 958 ms, render thread
- **Hot path:** `DispatchRender > CalcDotLuminanceFrontAsync > SceneNode.AddComponent > XRMesh Constructor > PopulateVertexData (remapped)`
- **Root cause:** Scene nodes with mesh-bearing components are still constructed after the first rendered frame, paying full CPU vertex population on the render/update thread.
- **Resolution:** Removed bootstrap deferral; model import now runs during world creation before first visible frame. `EnsureRenderPipelineVersionsCreated()` added to quad/cube framebuffers and UI batch meshes. GL mesh generation queue budget boosted during startup.
- **Validated:** `PopulateVertexData` and `SceneNode.AddComponent` absent from all post-fix profiler captures.

### [x] 2. Asset Explorer Synchronous File Enumeration — FIXED

- **Worst frame:** 420 ms, render thread (original); profiler now attributes ~120–380 ms to this scope but investigation confirmed this is **false attribution** from profiler ring buffer overflow during startup burst.
- **Hot path:** `EditorImGuiUI.RenderEditor > DrawAssetExplorerPanel > DrawAssetExplorerTab.GameProject > AssetExplorer.FileList.GameProject`
- **Root cause:** `DrawAssetExplorerFileList` previously called `Directory.GetDirectories` / `Directory.GetFiles` synchronously every frame inside the render loop.
- **Resolution:**
  - Directory snapshots cached per-path in `AssetExplorerTabState.DirectorySnapshots`.
  - Background `Task.Run` builds snapshots off the render thread; render path returns an empty placeholder until the build completes.
  - Path-targeted `FileSystemWatcher` invalidation instead of global cache clears.
  - Steady-state subsequent hits consistently 3–8 ms.
- **Profiler attribution note:** The first-frame "hit" (120–380 ms) is **not** Asset Explorer work — it's GL shader compilation / buffer creation from neighboring scopes that the profiler misattributes because its ring buffer overflows during the startup burst, dropping child scope events. Confirmed by `"Profiler overflow queue exceeded capacity; stale events discarded"` in logs and diagnostic sub-scope instrumentation showing zero self-time in the FileList code path.
- **Validated:** Diagnostic profiler scopes showed GetSnapshot and FilterEntries as sub-millisecond on first frame; subsequent frame hits are 3–8 ms consistently.

### [x] 3. Remaining Inline GL Mesh Generation — FIXED

- **Worst frame:** 413 ms, render thread
- **Hot path:** `GLMeshRenderer.Render.Generate > GLObject.Generate.VertexArray > GLRenderProgram.Link.LoadCachedBinary > GLObject.Generate.Buffer`
- **Root cause:** The draw path in `GLMeshRenderer.Rendering.cs` allowed inline `Generate()` for render-pipeline-priority meshes, bypassing the deferred queue.
- **Resolution:** Render-pipeline meshes now defer to the generation queue during startup throttling (`ThrottlePriorityGeneration`). Startup budget boost ensures they're processed quickly through `ProcessPendingUploads`. Precreation via `EnsureRenderPipelineVersionsCreated()` for critical meshes.
- **Validated:** `GLMeshRenderer.Render.Generate` absent from all post-fix profiler captures.

---

## P1 — High (50–130 ms single-frame impact)

### [x] 4. Pending Upload Burst — FIXED

- **Worst frame:** 131 ms, render thread
- **Hot path:** `XRWindow.ProcessPendingUploads > GLObject.Generate.VertexArray > GLRenderProgram.Link.GenerateShaders > GLObject.Generate.Shader`
- **Root cause:** The deferred upload path correctly batches work, but the first-frame burst still exceeds the frame budget. Shader compilation dominates. `GLUploadQueue.FrameBudgetMs` was only 2 ms with no startup boost — unlike `GLMeshGenerationQueue` which had `BoostBudgetUntilDrained`.
- **Resolution:** Added `BoostBudgetUntilDrained(double boostedMs)` to `GLUploadQueue` (mirroring the existing pattern in `GLMeshGenerationQueue`). Startup budget boosted to 50 ms in `OnStartupWindowAdded`, auto-restores to 2 ms when the pending queue drains. This allows shader compilation and buffer uploads to spread across more frames at a higher per-frame budget during the startup burst.
- **Done when:** `ProcessPendingUploads` stays under budget within a few frames of startup.

### [x] 5. UI Text Lazy Resource Creation — FIXED

- **Worst frame:** 51 ms, collect-visible thread
- **Hot path:** `CollectVisible_ScreenSpaceUI > UICanvasComponent.UpdateLayout > UITextComponent.VerifyCreated > CreateMaterial > TextStereo.vs deserialization`
- **Root cause:** `VerifyCreated()` and `CreateMaterial()` lazily create mesh, material, and SSBO on first layout/collect. Shader deserialization lands inside collect-visible. Each `UITextComponent` loads 3 shaders via `XRShader.EngineShader()` (synchronous disk I/O + deserialization).
- **Resolution:** Added `StartStartupTextShaderPrewarm()` that pre-loads all 7 common text shader variants (Text.vs, TextStereo.vs, TextRotatable.vs, TextRotatableStereo.vs, Text.fs, TextMsdf.fs, TextMtsdfScreen.fs) into the asset cache on a background `Task.Run` during early startup — alongside the existing font prewarm. The asset system caches loaded instances, so subsequent calls from `CreateMaterial` during collect-visible are near-instant lookups.
- **Done when:** No `TextStereo.vs` deserialization appears under `CollectVisible_ScreenSpaceUI`.

---

## Info — Symptom, Not Root Cause

### [x] CollectVisible Thread Stall (WaitForRender)

- **Worst frame:** 367 ms, collect-visible thread
- **Hot path:** `EngineTimer.CollectVisibleThread.WaitForRender`
- **Note:** Synchronization barrier. Blocked behind render-thread saturation. Fixes to items 1–5 should collapse this automatically.

---

## Attack Order

**For maximum startup improvement:**

1. Item 1 — late CPU mesh/component construction
2. Item 3 — inline GL generation
3. Item 4 — upload burst budget
4. Item 5 — UI text warmup
5. Item 2 — Asset Explorer caching

**For cleaner profiling data first:**

1. Item 2 — Asset Explorer caching
2. Item 1 — late CPU mesh/component construction
3. Item 3 — inline GL generation
4. Item 4 — upload burst budget
5. Item 5 — UI text warmup

---

## Validation

1. Cold startup capture with Asset Explorer **closed** (isolate engine).
2. Cold startup capture with GameProject tab **open** (measure editor overhead).
3. Compare per phase: max frame time in first 10 s, count of >100 ms drops, presence of hot paths above, `WaitForRender` magnitude.
4. Always validate cold starts — warm runs hide the exact cold-start work this list targets.

---

## Risks

- Render-pipeline-priority meshes cannot be blindly deferred; some must render the same frame or produce black output.
- Text warmup must track atlas rebuild / font invalidation to avoid stale GPU resources.
- Asset Explorer caching must not silently hide file changes.
- Captures include editor overhead; remeasure engine wins with and without heavy panels.
