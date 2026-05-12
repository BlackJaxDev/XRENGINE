# Render Submission Performance Debug & Optimization Plan

Status: Active (updated 2026-05-12)
Owner: Rendering

## 1. Problem

Two-Sponza scene, lights off, ImGui editor hidden:

- **CpuDirect**: ~80 fps. Healthy baseline.
- **GpuIndirectZeroReadback**: ~10 fps. 8x slower than CpuDirect on the same static scene.
- **GpuIndirectInstrumented**: also degraded; froze above ~28 commands before optimizations landed.

Goal: GPU-indirect paths within 20% of CpuDirect fps on B1 (two static Sponzas) and B2 (Sponzas + 100 skinned avatars).

## 2. Submission Paths

| Path | Per-frame cost driver |
| --- | --- |
| CpuDirect | `RenderCommandCollection.RenderCPU` → `GLMeshRenderer.Render`. State churn dominates. |
| GpuIndirectInstrumented | `HybridRenderingManager.DispatchRenderIndirect` + `GPUScene.SwapCommandBuffers`. |
| GpuIndirectZeroReadback | Same as instrumented + material-slot scatter/buckets. Production zero-readback path = `FullBucketScan`. |

Path A (skinned bounds direct-AABB write, `SkinnedBoundsGpuDirectAabbWrite`) is a separate optional feature, default OFF.

## 3. Measurement Workflow

### Setup

1. Build configuration: Release/profile for perf numbers; Debug only for triage.
2. Set `XRE_PROFILER_ENABLED=1` and `Engine.Profiler.RenderStallThresholdMs = 16f`.
3. Verify all debug switches in [Program.cs](../../../../XREngine.Editor/Program.cs) are OFF (`DumpIndirectArguments`, `ValidateBufferLayouts`, `LogCountBufferWrites`, `ProbeSourceCommandsBeforeCopy`, `ForceCpuIndirectBuild`, `DisableCountDrawPath`, `SkipIndirectTailClear`).
4. Warm shaders/programs; discard the first few seconds of capture.

### Required defaults

See live values in [Engine.Rendering.Settings.cs](../../../../XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs).

`EnableNvidiaDlss=false`, `EnableIntelXess*=false`, `GpuOcclusionCullingMode=Disabled`, `SkinnedBoundsRecomputePolicy=Never`, `CalculateSkinnedBoundsInComputeShader=false`, `SkinnedBoundsGpuDirectAabbWrite=false`, `UseGpuBvh=true`, `ZeroReadbackMaterialDrawPath=FullBucketScan`, `MsaaSampleCount=4`.

### Strategy switching

Use `XRE_FORCE_MESH_SUBMISSION_STRATEGY` env var (overrides settings). Values: `CpuDirect`, `GpuIndirectInstrumented`, `GpuIndirectZeroReadback`.

### Capture tasks

Run from VS Code:

- `Measurement-Baseline-CpuDirect`
- `Measurement-Baseline-GpuIndirectInstrumented`
- `Measurement-Baseline-GpuIndirectZeroReadback`
- `Measurement-PushSubDataBreakdown-GpuIndirectInstrumented` (sets `XRE_PUSHSUBDATA_BREAKDOWN=1`)

Logs land in `Build/Logs/<configuration>_<tfm>/<platform>/<session>/`:

- `profiler-fps-drops.log` — primary HotPath data.
- `profiler-render-stalls.log` — render-thread stalls.
- `profiler-main-thread-invokes.log` — MainThreadInvoke flooding.
- `log_opengl.txt` — `[BufferUploadAudit]` rows when breakdown env var is set.

### Scenes

- **B1** — two Sponzas (Sponza A `Uber`, Sponza B `Deferred`), lights off. Exercises forward+deferred together. Configured via [Assets/UnitTestingWorldSettings.jsonc](../../../../Assets/UnitTestingWorldSettings.jsonc).
- **B2** — B1 + 100 idle skinned avatars. Needed for Path A measurement.
- **B2-paused** — B2 with animation paused after warmup. Validates Path A dirty-skip.

### A/B toggles (one at a time)

`SkinnedBoundsGpuDirectAabbWrite`, `CalculateSkinnedBoundsInComputeShader`, `SkinnedBoundsRecomputePolicy`, mesh submission strategy, `UseGpuBvh`, `GpuOcclusionCullingMode`, vendor upscale flags.

### GPU-side validation

RenderDoc/Nsight on B1/B2: count `MultiDrawElementsIndirect[Count]`, `glUseProgram`, `glBindBufferBase`, `glBindVertexArray`, `glBufferSubData` per frame.

## 4. Findings Log

### P1 baseline (2026-05-10): GPU paths freeze above ~28 commands

CpuDirect ran clean. Both GPU-indirect strategies froze on two Sponzas. Render-thread queue grew unboundedly (queuedRender 7650; PushSubData waits 7–11 s). Single-Sponza (28 commands) GpuIndirectInstrumented was healthy. **Conclusion:** per-frame `PushSubData` traffic scaled super-linearly with command count and saturated the render thread.

### P1.5 attribution (2026-05-10): MeshAtlas full re-upload

With `XRE_PUSHSUBDATA_BREAKDOWN=1`, the flood resolved to a single buffer instance per attribute: `MeshAtlas_Dynamic_{Tangents,Positions,Normals,UV0,Indices}`. Peak 620 MB/s on Tangents alone, 21–28 uploads/s of the entire atlas.

Root cause: `GPUScene.RebuildAtlasIfDirty` called `state.Positions.PushSubData()` with no args (full buffer) every time the atlas was dirty, even though `AppendMeshToAtlas` only modifies the tail.

### P2 post-O-11 (2026-05-11): atlas fixed, hierarchy panel exposed

After O-11 landed, MeshAtlas pushes dropped to 24–128 bytes per dump. `XRViewport.SwapBuffers.MeshCommands` still reported 150–393 ms — but with `EmittedCommands: 0`, meaning the swap was a barrier wait, not work. Recovered top stalls on the render thread:

| Scope | Max ms | Cause |
| --- | ---: | --- |
| `UI.DrawWorldHierarchyTab` | 756 | ImGui hierarchy walking ~6000 nodes per frame. |
| `RenderCommand.Render` (bare) | 605 | Single CPU draw blocking — likely barrier wait. |
| `Invoke:GPUScene.LodTransitionBuffer.Initialize` | 4729 | Buffer init on render thread (one-shot, 5 s stall). |
| `Invoke:GLDataBuffer.PushSubData` | 1457 | Large single-range PushSubData. |

### P2.5 post-O-12/13/14 (2026-05-11): hierarchy + init fixed

Profiler-panel collector moved to app thread, hierarchy off-screen rows fast-skip, LOD init slimmed, MeshDataBuffer dirty-range tracker. Hierarchy max dropped 753 → 144 ms.

### P2.6 post-O-15/16/17 (2026-05-12): material maps gated, BVH spam suppressed

`MaterialAggregationFlags` and `MaterialSlotLookup` now dirty-gated by signature (down to 3 uploads/session vs per-frame). Removed hot `List<uint>` allocation. BVH failed-build spam dropped from 376 → 5. **Original 8x fps gap not yet closed** — measured ZeroReadback runs are stuck in shader warmup; one confirmation run crashed with `-1073740791` before steady state.

### Outstanding hypothesis

Bare `RenderCommand.Render` stall scales 1:1 with `QueuedRenderJobsNow` (542 jobs → 541 ms). Queue grows because the render thread blocks inside `RenderCommand.Render`, not because jobs are slow to drain. Suspect per-frame SSBO writes triggering implicit driver sync on the previous frame's GPU read. O-7 (per-draw barrier coalescing) is no longer the suspect — bare timing is invariant to it.

## 5. Optimization Backlog

### Landed

| ID | Change |
| --- | --- |
| O-11 | MeshAtlas subrange-append push (tail only, restart on resize). |
| O-12 | Drop redundant `PushSubData`/`MapBufferData` in `LodTransitionBuffer.Initialize`. |
| O-13 | Hierarchy panel: fast-skip off-screen leaves (`IsRectVisible` + `Dummy`). |
| O-14 | MeshDataBuffer dirty-range tracker (subrange push). |
| — | `RenderCommandCollection` dirty-delta queue (per-command `_dirty`/`_swapQueued`). |
| — | Profiler-panel collector moved to app thread @ 10 Hz. |
| O-15 | Audit per-frame SSBO writes for ring/fence/dirty gating (audit only). |
| O-16 | Profiler scopes inside `RenderCommand.Render` (swap, indirect build, scatter, multi-draw). |
| O-17 (partial) | Dirty-gate `MaterialAggregationFlags` + `MaterialSlotLookup` by signature; remove hot allocation; suppress repeated identical-count BVH rebuild failures. |

### Open — GPU-indirect static-scene stall (priority order)

| ID | Change | Predicted win | Risk |
| --- | --- | --- | --- |
| O-17 (finish) | Get a clean warmed ZeroReadback capture. Identify residual per-frame `glBufferSubData` from O-15 audit table. Apply ring-buffer or persistent-coherent + fence to remaining single-instance SSBOs (`MaterialTierIndirect*`, `MaterialTierDrawCounts`, `CulledSceneToRender`, scatter tables, indirect-build output). | Closes the 8x gap. | Ring-buffer correctness: readers must read the slot the CPU wrote this frame. Centralise slot selection in `GPUScene`. |
| O-6 | Version-stamp `_updatingCommandsBuffer` and `_updatingTransparencyMetadataBuffer`; skip `Memory.Move` + `PushSubData` in `SwapCommandBuffers` when versions match. | Removes dominant per-frame buffer cost in static GPU frames. | Do NOT include `_commandAabbBuffer` — separate lifetime, feeds BVH. |
| Extend O-11/O-14 | Tail-append for `RenderCommandsBuffer` resize uploads and `LodTableBuffer` scattered writes. | Smaller residual PushSubData traffic. | Low. |
| O-9 | Cache `CoalesceContiguousBatches` output by command-list version. | Removes per-frame batch list allocation. | Low. |
| O-8 | Frame-stable submission plan for CpuDirect (`SubmissionEntry[]` built at commit). | Better state grouping; lower collect/sort cost. | Medium. |
| O-7 | Coalesce `MemoryBarrier` calls (one per pass instead of per draw). **Deprioritised** — bare `RenderCommand.Render` is invariant to per-draw barriers. | Reduces driver serialization. | Correctness if compute interleaves writes. Default-off behind opt-in flag. |

### Open — Path A (skinned bounds direct-AABB write)

Only meaningful after P0 activation contract is proven. Default-off until then.

| ID | Change |
| --- | --- |
| P0-1 | Fix Path A activation contract. Today `TryComputeFromPrepassOutput` returns empty positions on GPU-only success, and `ApplySkinnedBoundsResult` treats that as failure. Split CPU-positions-cached from bounds-computed states, or pass an explicit success flag. |
| O-1 | Reuse Path A registry snapshot buffer in `SkinnedMeshBoundsCalculator.RefreshAllSkinnedAabbs` (kill the `RenderableMesh[]` per-call alloc). |
| O-2 | Per-mesh dirty tracking; skip Path A work for clean meshes (validates on B2-paused). |
| O-3 | Gate `RefreshAllSkinnedAabbs` behind actual BVH need (build/refit). |
| O-4 | Reduce-once-per-renderer, scatter to command slots. Removes duplicate full-vertex reductions for multi-submesh meshes. |
| O-5 | Batch contiguous sentinel writes into one `PushSubData` per run instead of N tiny 32-byte calls. |
| O-10 | Replace CPU sentinel writes with a compute kernel; one dispatch per acceleration-structure refresh. |

## 6. Next Steps

1. **Get a clean warmed ZeroReadback capture.** Skip warmup frames (first ~5 s of shader compile). Reproduce or isolate the `-1073740791` exit.
2. **Read recovered leaves from O-16 scopes** to identify which SSBO write inside `RenderCommand.Render` is causing the implicit driver sync.
3. **Apply O-17 finish** (ring-buffer or fence-gate the identified buffer).
4. **Re-measure** B1 ZeroReadback fps vs CpuDirect; target within 20%.
5. After P2.6 closes, move to Path A (P0-1 first, then O-1..O-5, O-10).

## 7. Risks

- Path A activation fix must not break consumers needing CPU skinned vertex positions (BVH/raycast). Make GPU-only success explicit.
- Per-mesh dirty tracking can produce stale culling. DEBUG asserts comparing dirty revisions to dispatched revisions.
- O-6 version-stamp must cover every copied payload, or stale frames render. Centralise increment helpers; DEBUG asserts on direct writes. Do NOT fold `_commandAabbBuffer` into the same gate.
- O-7 barrier coalescing is a correctness footgun; default-off, per-batch opt-in.
- O-10 compute sentinel seeding must run before BVH refresh and after any compute writing positions; encode ordering in `VPRC_BuildAccelerationStructure`.
- Ring-buffer (O-17): readers must read the slot the CPU wrote this frame. Bind by slot index, not buffer handle.

## 8. Validation Checklist

- [ ] Measurement preset output records build config, strategy, zero-readback path, debug flags, `UseGpuBvh`, Path A setting, upscale flags.
- [ ] Captures taken after warmup; first warmup frames excluded from medians.
- [ ] `profiler-fps-drops.log` HotPath delta vs baseline captured per phase.
- [ ] Report-NewAllocations diff shows zero new hot-path allocations.
- [ ] Nsight frame: indirect call count, barrier count, `glUseProgram`/`glBindVertexArray`/`glBufferSubData` counts vs baseline.
- [ ] Strict zero-readback validation uses `FullBucketScan` only.
- [ ] `Test-VulkanPhase3-Regression` and `Test-SurfelGi` green.
- [ ] Golden-image parity B1 + B2 vs pre-Path-A baseline.
- [ ] [docs/architecture/rendering/default-render-pipeline-notes.md](../../../architecture/rendering/default-render-pipeline-notes.md) updated if invariants change.

## 9. Related Code

- [HybridRenderingManager.cs](../../../../XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs)
- [GPUScene.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPUScene.cs)
- [RenderCommandCollection.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/RenderCommandCollection.cs)
- [SkinnedMeshBoundsCalculator.cs](../../../../XREngine.Runtime.Rendering/Rendering/Compute/SkinnedMeshBoundsCalculator.cs)
- [RenderableMesh.cs](../../../../XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.cs)
- [VPRC_BuildAccelerationStructure.cs](../../../../XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_BuildAccelerationStructure.cs)
- [Engine.CodeProfiler.cs](../../../../XRENGINE/Engine/Subclasses/Engine.CodeProfiler.cs)
- [Engine.Rendering.Settings.cs](../../../../XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs)
- [docs/architecture/rendering/mesh-submission-strategies.md](../../../architecture/rendering/mesh-submission-strategies.md)
