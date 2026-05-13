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

`EnableNvidiaDlss=false`, `EnableIntelXess*=false`, `GpuOcclusionCullingMode=GpuHiZ`, `SkinnedBoundsRecomputePolicy=Never`, `CalculateSkinnedBoundsInComputeShader=false`, `SkinnedBoundsGpuDirectAabbWrite=false`, `UseGpuBvh=true`, `ZeroReadbackMaterialDrawPath=FullBucketScan`, `MsaaSampleCount=4`.

When `CpuDirect` is forced with `GpuOcclusionCullingMode=GpuHiZ`, CPU mesh draws consume a previous compatible Hi-Z visibility snapshot from a GPU command mirror. The cull dispatch runs zero-readback; the only CPU readback is the next-frame snapshot publication, with temporal hysteresis before CPU draws are skipped.

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
- `profiler-indirect-calls.log` (new, see §3.x) — per-frame indirect call census + GL state-bind counts.

### Required new logging (P3)

The data needed to confirm or reject the P3 bucket-fan-out hypothesis is not currently captured. Add the following before the next measurement pass; all default OFF and aggregate to once-per-second rolling log rows.

1. **Indirect call census** — instrument `DispatchRenderIndirectCountBucket` (and the `DispatchRenderIndirectRange` callers) in [HybridRenderingManager.cs](../../../../XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs):
   - Per pass: `passName`, `materialSlotIds.Count`, `materialTierCount`, `bucketsIssued`, `bucketsSkippedEmpty`, `bucketsSkippedConfigure`, `programSwitches`, `vaoSwitches`, `setUniformsCalls`.
   - Aggregate into a single log line per frame; flush at 1 Hz.
2. **GL state-bind counters** — increment counters in `GLRenderer` for `glUseProgram`, `glBindBufferBase`, `glBindVertexArray`, `glBufferSubData`, `glMemoryBarrier`. Snapshot to the same row. Compare CpuDirect vs ZeroReadback head-to-head; a 10×+ delta on `glUseProgram` or `glBindBufferBase` confirms bucket fan-out as the dominant cost.
3. **GPU-side timer queries** — wrap with `glQueryCounter(GL_TIMESTAMP)`:
   - the whole material-tier bucket loop in `DispatchRenderIndirectMaterialTiers`,
   - `BuildMaterialScatter` compute,
   - `BuildIndirectCommands` compute,
   - `GPUScene.SwapCommandBuffers`.
   - Read back N-frames-late to avoid stalls; log medians. Without GPU timestamps we cannot tell whether the render-thread block is GPU-bound or driver-sync-bound.
4. **Bisect env vars** (default off):
   - `XRE_BUCKET_LOOP_DRY_RUN=1` — execute the slot/tier loop but skip the final `MultiDrawElementsIndirect[Count]` call. If fps recovers near CpuDirect, GL/driver submission cost dominates (validates P3-A below).
   - `XRE_BUCKET_LOOP_SKIP_EMPTY=1` — apply the CPU-side active-slot mask early (see O-18). Should be a no-op for correctness, big win if P3-A is right.
   - `XRE_SKIP_COMMAND_SWAP_IF_CLEAN=1` — short-circuit `SwapCommandBuffers` when content version is unchanged. Validates O-6 cheaply before implementing the full version-stamp gate.
   - `XRE_FORCE_SINGLE_BUCKET=1` — route every draw through bucket (slot=0, tier=0). Strictly for isolating per-bucket overhead from per-draw cost.

### Scenes

- **B1** — two Sponzas (Sponza A `Uber`, Sponza B `Deferred`), lights off. Exercises forward+deferred together. Configured via [Assets/UnitTestingWorldSettings.jsonc](../../../../Assets/UnitTestingWorldSettings.jsonc).
- **B2** — B1 + 100 idle skinned avatars. Needed for Path A measurement.
- **B2-paused** — B2 with animation paused after warmup. Validates Path A dirty-skip.

### A/B toggles (one at a time)

`SkinnedBoundsGpuDirectAabbWrite`, `CalculateSkinnedBoundsInComputeShader`, `SkinnedBoundsRecomputePolicy`, mesh submission strategy, `UseGpuBvh`, `GpuOcclusionCullingMode`, vendor upscale flags.

### GPU-side validation

RenderDoc/Nsight on B1/B2: count `MultiDrawElementsIndirect[Count]`, `glUseProgram`, `glBindBufferBase`, `glBindVertexArray`, `glBufferSubData` per frame.

## 4. Findings Log

### P4 regression sweep (2026-05-12): GpuHiZ-as-default fallout

Background: making `GpuOcclusionCullingMode=GpuHiZ` the default ran the CPU-side
Hi-Z visibility plumbing in `CpuDirect`. That code path was incomplete and
produced four interacting regressions that together crushed CpuDirect from
~80 fps to ~2-9 fps. **Do not undo these fixes without replacing them.**

| # | Symptom | Root cause | Fix (do NOT revert) | Guarding flag |
| --- | --- | --- | --- | --- |
| 1 | `RenderResourceRegistry.TryGetTexture` throws `InvalidOperationException` ("Operations that change non-concurrent collections must have exclusive access") every frame | Render thread reads `_textures` Dictionary while worker threads register descriptors | `_textures`/`_frameBuffers`/`_buffers`/`_renderBuffers` are now `ConcurrentDictionary`; register paths use `GetOrAdd`, remove paths use `TryRemove` | n/a (always on) |
| 2 | `GpuBvhTree.Build` overflow logged every frame (`primitives=52, nodes=103`); CpuDirect dropped to ~2 fps once meshes appeared | `GPUScene.PrepareBvhForCulling` re-built on every Add/Remove even when post-mutation `commandCount` matched the count that just overflowed → infinite retry+log | Added "same-count overflow" short-circuit that clears dirty and returns. Next genuine count change still unsuppresses normally | n/a |
| 3 | Deferred meshes render black, forward uber meshes invisible, only resolves when camera moves fast | CPU Hi-Z visibility snapshot's static-scene short-circuit reused stale snapshot. Meshes absent from snapshot tick down `TemporalOcclusionHysteresisFrames=2` and are CPU-culled. Fast camera motion invalidated snapshot compatibility, masking the bug | (a) Removed the static-scene short-circuit in `PrepareCpuHiZVisibility`. (b) **Gated CPU-side trust of the Hi-Z snapshot behind `XRE_CPU_HIZ_OCCLUSION=1`** because the underlying GPU cull-output / source-command-decode is still under-reporting visible meshes | `XRE_CPU_HIZ_OCCLUSION` (default off) |
| 4 | CpuDirect baseline stuck at ~7-9 fps even with occlusion disabled | `VisualScene3D.ShouldMaintainCpuGpuCommandMirror()` forced the full CPU↔GPU command mirror (per-frame command buffer upload + BVH build) whenever `CpuDirect + GpuHiZ`, but with #3 gated off nothing consumed the mirror — pure dead weight | Mirror now only enabled when `XRE_CPU_HIZ_OCCLUSION=1` (or Surfel-GI is active) | `XRE_CPU_HIZ_OCCLUSION` |

Latent issue still open: the GPU cull pass output feeding
`_culledSceneToRenderBuffer` is missing mesh commands that should be visible
(the actual reason #3's snapshot is unsafe). Suspects: Hi-Z over-cull against
stale reprojected depth, BVH-fallback path (#2) skipping commands, or
`command.Reserved1 → IRenderCommandMesh` decode in
`GPURenderPassCollection.Occlusion.PublishCpuHiZVisibilitySnapshotIfReady`.
Until that's diagnosed and corrected, `XRE_CPU_HIZ_OCCLUSION` MUST stay
default-off. When you fix it, also flip the mirror gate back on.

Anti-regression rules going forward:

1. The render thread must NEVER read a plain `Dictionary<,>` that a worker
   thread can mutate. New caches in `RenderResourceRegistry`-shaped classes
   must use `ConcurrentDictionary` or an explicit lock.
2. Any GPU job whose result is consumed by CPU draw decisions must be
   *correctness*-validated against the non-GPU path on a static and a
   churning scene before becoming a default. Performance wins from skipping
   CPU work are invisible-mesh bugs if the skip set is wrong.
3. Per-frame BVH/AABB upload + GPUScene command mirror are **opt-in**, gated
   on the actual consumer being enabled. Never wire them on by side-effect
   of a settings default flipping.
4. Any GPU dispatch whose result feeds a same-frame CPU readback must use
   the fence-N-late pattern in `PublishCpuHiZVisibilitySnapshotIfReady`
   (insert `FenceSync` post-dispatch, non-blocking `ClientWaitSync(0)` on
   publish, defer one frame if not signaled). Synchronous readback inside
   the render frame collapses fps to ~7 Hz immediately.

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

### P3 hypothesis (2026-05-12): zero-readback bucket fan-out dominates

Post P2.6, per-frame buffer-byte traffic is small but the 8x gap persists. The remaining structural difference between CpuDirect and `FullBucketScan` is in the **dispatch loop shape**, not buffer uploads:

`DispatchRenderIndirectMaterialTiers` in [HybridRenderingManager.cs](../../../../XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs) walks `materialSlotIds.Count × MaterialTierCount` buckets per render pass and calls `MultiDrawElementsIndirectCount` on **every** bucket regardless of whether that (material, tier) actually has any commands this frame. Two Sponzas import ~25 unique materials each ⇒ ~50 slots × `MaterialTierCount` tiers × multiple passes (depth-prepass, gbuffer, forward, shadow, transparent) ⇒ hundreds–thousands of indirect draws/frame versus ~28 for CpuDirect.

Each `MultiDrawElementsIndirectCount` sources its draw count from a GPU-written SSBO (`MaterialTierDrawCounts`), which forces the driver to **implicitly synchronise against the scatter compute that wrote those counts**. The single coalesced `MemoryBarrier` ahead of the loop (O-7) does not eliminate this — every empty-bucket draw still costs a `glUseProgram` / `glBindVertexArray` / state-set + driver count sync.

Matches every observed symptom:

- Bare `RenderCommand.Render` timing scales with queued jobs (jobs back up because the call inside doesn't return).
- O-7 (per-draw barrier coalesce) had no effect — barrier wasn't the cost.
- Per-frame `glBufferSubData` is now tiny but fps is unchanged.
- CpuDirect at ~28 commands matches the bucket count of a single-Sponza healthy run.

**P3-A — primary suspect:** bucket fan-out dispatches dominate driver/CPU cost on the render thread.
**P3-B — secondary suspect:** swap-time `Memory.Move + PushSubData(0, byteCount)` on `_updatingCommandsBuffer` triggers an implicit driver sync against the previous frame's compute read of `RenderCommandsBuffer`, even when content is identical (motivates O-6 / O-21).

Confirm with the §3 "Required new logging (P3)" instrumentation before implementing O-18..O-21.

### Outstanding hypothesis

Bare `RenderCommand.Render` stall scales 1:1 with `QueuedRenderJobsNow` (542 jobs → 541 ms). Queue grows because the render thread blocks inside `RenderCommand.Render`, not because jobs are slow to drain. Suspect per-frame SSBO writes triggering implicit driver sync on the previous frame's GPU read. O-7 (per-draw barrier coalescing) is no longer the suspect — bare timing is invariant to it.

P3 (above) refines this: the implicit-sync cost is not from buffer *writes* but from `MultiDrawElementsIndirectCount` reading GPU-written count buffers across hundreds of empty buckets per frame. O-18 (active-slot CPU mask) is the cheapest test; O-21 (persistent-coherent ring-mapped command buffer) addresses the residual sync if P3-B is real.

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
| O-18 | **Active-slot CPU mask.** At commit/collect time (where each command's `materialSlotId` is already known), maintain a per-pass `BitArray`/packed bitmask of slots that have ≥1 command. `DispatchRenderIndirectMaterialTiers` skips bucket iterations whose slot bit is clear. Pure CPU knowledge — not a readback, zero-readback contract preserved. | Largest expected win on B1: collapses the slot loop from O(allMaterials) to O(usedMaterials in this pass). | Low. Stale mask = missed draws; assert mask version matches command-collection version. Validate with `XRE_BUCKET_LOOP_SKIP_EMPTY=1`. |
| O-19 | **Per-pass slot pre-filter.** Build the `materialSlotIds` slice per `RenderPass` at commit time and store on `GPURenderPassCollection`. Today the loop iterates all slots and `continue`s on pass mismatch after doing material/program work. | Removes the per-slot `RenderPass` check and dead program/state work. | Low. Slot lists must invalidate when material `RenderPass` changes. |
| O-20 | **State-grouped slot order.** Sort each pass's slot list once at commit by `(program, VAO, depth/blend state)`; cache by command-version. The current loop calls `UseProgram` / `ConfigureIndirectRendererForTier` per slot regardless of redundancy. | Cuts redundant `glUseProgram` / VAO binds inside the bucket loop. | Low. Order-dependent state must not leak across slots (depth-test, blend). |
| O-6 | Version-stamp `_updatingCommandsBuffer` and `_updatingTransparencyMetadataBuffer`; skip `Memory.Move` + `PushSubData` in `SwapCommandBuffers` when versions match. Validate cheaply with `XRE_SKIP_COMMAND_SWAP_IF_CLEAN=1` before the full implementation. | Removes dominant per-frame buffer cost in static GPU frames; eliminates an implicit driver sync against the previous-frame compute consumer (P3-B). | Do NOT include `_commandAabbBuffer` — separate lifetime, feeds BVH. |
| O-21 | **Persistent-coherent ring-mapped `RenderCommandsBuffer`.** Replace the `Memory.Move + glBufferSubData(0, byteCount)` swap with a 2- or 3-slot persistent-coherent mapped region; CPU writes slot `frame % N`, shaders read by slot index bound this frame. Fences on the slot before reuse. | If P3-B is real (driver implicit-sync on the swap-time `glBufferSubData`), this is the structural fix. | Ring correctness: readers must bind the slot the CPU wrote *this* frame. Centralise slot selection in `GPUScene`. DEBUG asserts. |
| O-17 (finish) | Get a clean warmed ZeroReadback capture. Identify residual per-frame `glBufferSubData` from O-15 audit table. Apply ring-buffer or persistent-coherent + fence to remaining single-instance SSBOs (`MaterialTierIndirect*`, `MaterialTierDrawCounts`, `CulledSceneToRender`, scatter tables, indirect-build output). | Closes the residual gap after O-18..O-21. | Ring-buffer correctness: readers must read the slot the CPU wrote this frame. Centralise slot selection in `GPUScene`. |
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

1. **Land the P3 instrumentation** in §3 (indirect call census, GL state-bind counts, GPU timestamps, four bisect env vars). One-pass change to `HybridRenderingManager.cs` + `GLRenderer`, default off, 1 Hz aggregation.
2. **Capture B1 ZeroReadback warmed** with the new logging + GPU timestamps. Compare to CpuDirect head-to-head.
3. **Read three numbers** to confirm/reject P3-A:
   - `bucketsIssued` vs `bucketsSkippedEmpty` (most should be skippable),
   - `glUseProgram` delta ZeroReadback vs CpuDirect,
   - GPU timestamp delta on the bucket-loop vs CPU wall time on the same scope.
4. **Validate cheaply** with `XRE_BUCKET_LOOP_DRY_RUN=1` and `XRE_BUCKET_LOOP_SKIP_EMPTY=1`. If skip-empty alone closes most of the gap, ship O-18 first.
5. **Implement in order:** O-18 → O-19 → O-20 → (if residual) O-6 → O-21 → O-17 finish.
6. **Re-measure** B1 ZeroReadback fps vs CpuDirect after each; target within 20%.
7. After the gap closes, move to Path A (P0-1 first, then O-1..O-5, O-10).

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

## 10. Forward Architecture: Path-Isolated Occlusion Culling

After the P4 regression sweep we are committing to a clean split: **each
submission path owns its occlusion culling end-to-end. No cross-path data
dependencies.**

### 10.0 Reference algorithms

Required reading before touching the GPU Hi-Z path:

- [Rastergrid — Hierarchical-Z map based occlusion culling (2010)](https://www.rastergrid.com/blog/2010/10/hierarchical-z-map-based-occlusion-culling/)
  — canonical algorithm: build max-depth mip pyramid from the depth
  buffer; per-object sample 4 max-depth texels at LOD chosen so the
  object's screen footprint covers ≤2×2 texels; compare to object's
  nearest depth; cull on GPU only.
- [Nick Darnell — Hierarchical Z-Buffer Occlusion Culling (2010)](https://www.nickdarnell.com/hierarchical-z-buffer-occlusion-culling/)
  — practical refinement: dedicated low-res (e.g. 512×256) occluder pass
  feeds mip 0 (don't reuse the main depth), compute-shader cull per
  bounding sphere, NDC corner sampling that handles perspective
  distortion better than the AMD original, `[numthreads(...)]` care.
- [Nick Darnell — HZB Occlusion Culling: Shadows (2010)](https://www.nickdarnell.com/hierarchical-z-buffer-occlusion-culling-shadows/)
  — caster culling: build a 2nd HZB from the light, cull casters in
  light space, then extrude bbox shadow volume (min-depth points
  re-projected via max light-HZB sample) and re-test against the
  player's HZB.

What our codebase already has that matches these (do NOT rewrite from scratch):

| Reference step | Our implementation |
| --- | --- |
| Hi-Z map mip-pyramid (max-depth reduce) | `BuildHiZPyramid` + `_hiZDepthPyramid` + `_hiZInitProgram` / `_hiZGenProgram` ([GPURenderPassCollection.Occlusion.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection.Occlusion.cs)). Source today is the main depth attachment (rastergrid mode), not a dedicated occluder RT (Darnell mode). |
| GPU compute cull comparing AABB-derived depth vs 2×2 Hi-Z max | `_hiZOcclusionProgram` (`Compute/Occlusion/GPURenderOcclusionHiZ.comp`) dispatches per command, writes `CulledSceneToRenderBuffer` + `_culledCountBuffer` + `_occlusionOverflowFlagBuffer` |
| GPU-driven indirect draw count | `glMultiDrawElementsIndirectCount` path gated by `IndirectParityChecklist.UsesCountDrawPath` in [HybridRenderingManager.cs](../../../../XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs) (real flag `DebugSettings.DisableCountDrawPath` on [GPURenderPassCollection.Core.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection.Core.cs); older notes referenced a `_isDrawCountAllowedToBeFromGpu` symbol that no longer exists) |
| Caster shadow culling via 2nd HZB | NOT IMPLEMENTED — see C-GPU-6 below |
| CPU-side hardware-query occlusion (NOT a Hi-Z technique) | `CpuRenderOcclusionCoordinator` ([Rendering/Occlusion/CpuRenderOcclusionCoordinator.cs](../../../../XREngine.Runtime.Rendering/Rendering/Occlusion/CpuRenderOcclusionCoordinator.cs)) — GL `AnySamplesPassedConservative` queries with temporal hysteresis |

Important correctness note: **what we used to call "CPU Hi-Z" was not a
Hi-Z algorithm.** It was the CPU reading the GPU compute Hi-Z cull's
output buffer (`_culledSceneToRenderBuffer`) and applying temporal
hysteresis to decide whether to skip CPU draws. That coupling is what
broke during the P4 sweep. The actual Hi-Z algorithm (rastergrid/Darnell)
runs entirely on the GPU and never returns visibility to CPU. The
CpuDirect path therefore should NOT use Hi-Z at all — it should use
either no occlusion, hardware occlusion queries
(`CpuRenderOcclusionCoordinator`), or a CPU software-rasterizer
occluder (Masked SOC) — never the GPU Hi-Z output.

### 10.1 Design rules

1. `CpuDirect` MUST NOT consume any GPU compute cull output. No
   `_culledSceneToRenderBuffer` reads, no GPU command mirror, no fence
   waits inside the render frame. Occlusion is CPU-only.
2. `GpuIndirectZeroReadback` MUST NOT read back to CPU for visibility.
   Cull, count, and draw are entirely GPU-side; the host issues a single
   `glMultiDrawElementsIndirectCount` with the parameter buffer on GPU.
3. `GpuIndirectInstrumented` is allowed to read back (it exists for
   debugging) but only at end-of-frame, never on the critical path.
4. The `EOcclusionCullingMode` enum is interpreted **per submission path**.
   A mode that doesn't make sense for the active path silently degrades
   (e.g. `GpuHiZ` selected while CpuDirect forced → falls back to
   `CpuQueryAsync`, never to GPU-snapshot consumption).

### 10.2 CpuDirect occlusion (target design)

Active components (already in tree, just need to be the default):
- `CpuRenderOcclusionCoordinator` ([Rendering/Occlusion/CpuRenderOcclusionCoordinator.cs](../../../../XREngine.Runtime.Rendering/Rendering/Occlusion/CpuRenderOcclusionCoordinator.cs))
  driving GL `AnySamplesPassedConservative` hardware occlusion queries with
  per-mesh temporal hysteresis. Vulkan path is conservative-visible until
  command-buffer integration lands.
- `AsyncOcclusionQueryManager` for previous-frame result resolution.
- Standard view-frustum cull stays on the visible-collection pass.

Note: this is the "naive occlusion query" pattern rastergrid/Darnell
explicitly cite as the slower predecessor to GPU Hi-Z. It is fine for
the CpuDirect path because (a) we have it wired up, (b) CpuDirect has
no compute pass to consume a Hi-Z buffer, (c) using previous-frame
results + hysteresis keeps the CPU off the critical sync.

Todos:

- [x] **C-CPU-1**: Make `Engine.Rendering.Settings.GpuOcclusionCullingMode`
  getter path-aware. When `Engine.Rendering.ResolveMeshSubmissionStrategy()
  == CpuDirect`, coerce any `GpuHiZ` return to `CpuQueryAsync`. Keep raw
  field intact so swapping strategies at runtime still produces the
  right effective mode. Place coercion alongside the existing
  `XRE_OCCLUSION_CULLING_MODE` env override in
  [Engine.Rendering.Settings.cs](../../../../XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs#L1366).
- [x] **C-CPU-2**: Delete CPU Hi-Z snapshot machinery once C-CPU-1 lands.
  Remove from
  [GPURenderPassCollection.Occlusion.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection.Occlusion.cs):
  `PrepareCpuHiZVisibility`, `TryGetCpuHiZVisibility`,
  `PublishCpuHiZVisibilitySnapshotIfReady`, `IsCpuHiZSnapshotCompatible`,
  `_cpuHiZVisibleMeshCommands`, `_cpuHiZTemporalOcclusion`,
  `_cpuHiZPendingFence` and friends. Remove
  `s_cpuHiZOcclusionEnabled` + `XRE_CPU_HIZ_OCCLUSION` plumbing from
  [RenderCommandCollection.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/RenderCommandCollection.cs)
  and the CpuDirect+GpuHiZ branch in
  [VisualScene3D.ShouldMaintainCpuGpuCommandMirror](../../../../XREngine.Runtime.Rendering/Rendering/VisualScene3D.cs#L204).
- [ ] **C-CPU-3** (scaffolded; rasterizer deferred): CPU software-rasterizer
  occluder pass (Masked SOC port) for occluder-driven cull without
  query latency. **Scaffold landed**:
  [CpuSoftwareOcclusionCuller.cs](../../../../XREngine.Runtime.Rendering/Rendering/Occlusion/CpuSoftwareOcclusionCuller.cs)
  with `BeginFrame`/`SubmitOccluder`/`TestVisible` API, opt-in via
  `Engine.EffectiveSettings.EnableCpuSoftwareOcclusionCulling` /
  `XRE_CPU_SOC_OCCLUSION=1` env override, telemetry counters
  `OcclusionTelemetry.CpuSocTested` / `CpuSocCulled`. Rasterizer body is
  intentionally a no-op (`TestVisible` returns true conservatively); a
  faithful Masked SOC port is multi-thousand lines of SIMD-heavy code
  and is the follow-up. Wiring into cull decision sites and occluder
  geometry tagging is also pending. Lower priority than C-GPU-* items
  because hardware queries cover most cases.
- [x] **C-CPU-4**: Stable per-command identity for CPU occlusion queries.
  Added `RenderCommand.StableQueryKey` (uint, assigned at construction
  via `Interlocked.Increment`) in
  [RenderCommand.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/RenderCommand.cs).
  `CpuRenderOcclusionCoordinator` lookups in `RenderCommandCollection`
  ([RenderCPU](../../../../XREngine.Runtime.Rendering/Rendering/Commands/RenderCommandCollection.cs)
  and `RenderCPUFiltered`) now key by `cmd.StableQueryKey` instead of
  the foreach position. Because identity is stable across insert/remove,
  the per-mutation soft-reset in `BeginPass` was removed; the existing
  `StaleEvictionFrames`-based eviction reclaims orphaned `QueryState`
  entries from removed commands. Acceptance: a mesh insert/remove no
  longer disturbs surviving meshes' `LastAnySamplesPassed` state, and
  no full-draw frame follows a mutation.

### 10.2.x Observability (C-OBS series)

User-visible verification that occlusion culling is actually doing work
on both the CPU-query path and the GPU Hi-Z path. Without this, every
subsequent perf optimization is blind.

- [x] **C-OBS-1**: Add lightweight per-frame occlusion telemetry facility
  ([OcclusionTelemetry.cs](../../../../XREngine.Runtime.Rendering/Rendering/Occlusion/OcclusionTelemetry.cs)).
  Tracks `CpuTested`, `CpuCulled`, `CpuRendered`, `GpuCandidates`,
  `GpuOccluded`, plus active mode + submission strategy. Snapshot/reset
  via `BeginFrame()` invoked from
  [Engine.Rendering.Stats.BeginFrame](../../../../XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Stats.cs).
  No GPU readbacks introduced; GPU counts honor existing
  `IsCpuReadbackCountDisabledForPass()` gate and report unavailability
  rather than stalling.
- [x] **C-OBS-2**: Wire CPU-query path. `RenderCommandCollection.RenderCPU`
  calls `RecordCpuPassBegin` at pass start and `RecordCpuCulledOne` for
  every command the `CpuRenderOcclusionCoordinator.ShouldRender` skips.
- [x] **C-OBS-3**: Wire GPU Hi-Z path. `ApplyGpuHiZOcclusion` records
  candidates/occluded per pass plus the readback-availability flag so the
  UI can clearly distinguish "no occlusion" from "occlusion happening but
  unobservable on zero-readback strategy".
- [x] **C-OBS-4**: Add Editor → View → **Occlusion** ImGui panel
  ([EditorImGuiUI.OcclusionPanel.cs](../../../../XREngine.Editor/IMGUI/EditorImGuiUI.OcclusionPanel.cs))
  showing live tested/culled/rendered per path, percentages, and an
  explicit message when the zero-readback strategy hides GPU counts
  (suggests switching to `GpuIndirectInstrumented` to verify).

Acceptance: with the editor running on CpuDirect+CpuQueryAsync the panel
shows non-zero `Culled` numbers within a few seconds of moving the camera
to face an occluder; on GpuIndirectInstrumented+GpuHiZ it shows non-zero
`Occluded` from the compute cull; on GpuIndirectZeroReadback+GpuHiZ it
clearly says counts are unavailable (rather than misleading zeros).


Acceptance: CpuDirect baseline regains its ~80 fps headroom on B1; no GPU
buffer is mapped during the CpuDirect frame; profiler shows zero render
stalls attributed to occlusion bookkeeping.

### 10.3 GPU zero-readback occlusion (target design)

Active components:
- `GpuBvhTree` ([Rendering/Compute/GpuBvhTree.cs](../../../../XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.cs))
  GPU-resident BVH (Morton + optional SAH refine), broad-phase reject.
- `VPRC_BuildAccelerationStructure` builds/refits the BVH per-frame from
  per-command world AABBs.
- `_hiZDepthPyramid` + `BuildHiZPyramid` — the rastergrid/Darnell max-depth
  mip pyramid, built from the main depth attachment.
- `GPURenderOcclusionHiZ.comp` (`_hiZOcclusionProgram`) — compute cull
  that samples the pyramid per command and writes
  `CulledSceneToRenderBuffer` + `_culledCountBuffer`.
- Indirect draw: `glMultiDrawElementsIndirectCount` consumes the
  GPU-written count buffer directly.

This already follows the Darnell/rastergrid architecture. The work below
is correctness + sizing, not rewrites.

Todos:

- [x] **C-GPU-1**: Audit every `MapBufferData()` / `GetDataRawAtIndex` /
  `ReadUIntAt` call reachable from the zero-readback frame path. Any
  that fires when `MeshSubmissionStrategy == GpuIndirectZeroReadback`
  and the relevant `IndirectDebug.*` switch is OFF is a bug.
  Catalogue + delete or guard. Read-only audit; cheap; informs the
  rest of this list.
- [x] **C-GPU-2**: Verify the GPU count-draw path is never overridden by a
  diagnostic switch in production. Real gating chain is
  `IndirectParityChecklist.UsesCountDrawPath`
  ([HybridRenderingManager.cs](../../../../XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs)),
  which combines `DrawIndirectBufferBindingReady`, `ParameterBufferBindingReady`,
  `IndexedVaoValid`, `SupportsIndirectCountDraw`, and `!DebugSettings.DisableCountDrawPath`
  on [GPURenderPassCollection.Core.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection.Core.cs).
  (Older notes referenced `_isDrawCountAllowedToBeFromGpu`; that symbol does
  not exist — corrected.) Added `[Conditional("DEBUG")]` invariants:
  `AssertZeroReadbackUsesGpuCountPath` and `AssertZeroReadbackProductionInvariants`
  in `HybridRenderingManager`, wired into all three dispatch sites
  (`DispatchRenderIndirect`, `DispatchRenderIndirectRange`, `DispatchRenderIndirectCountBucket`),
  plus `AssertZeroReadbackProductionInvariantsForPass` in
  `GPURenderPassCollection.CapturePassPolicySnapshot`. Asserts fire if any of
  `DisableCountDrawPath`, `ForceCpuFallbackCount`, `ForceCpuIndirectBuild`,
  `!DisableCpuReadbackCount`, or `EnableCpuBatching` is active while the
  pass strategy is `GpuIndirectZeroReadback`, or if the parity checklist
  reports a non-count-draw path. Compiles out of Release.
- [ ] **C-GPU-3**: Fix the latent GPU-cull undercount that motivated
  `XRE_CPU_HIZ_OCCLUSION=off`. Three suspects, ranked:
  1. `command.Reserved1 → IRenderCommandMesh` decode via
     `TryGetSourceCommand` going stale after Add/Remove churn.
     **Update (2026-05-12)**: audited — `TryGetSourceCommand` is now
     only consumed by the indirect-text-glyph batch preparation path
     in [HybridRenderingManager.cs](../../../../XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs#L2990).
     The deleted CPU Hi-Z snapshot was the other consumer (C-CPU-2).
     Lookup correctness still matters for text rendering but is not
     a Hi-Z undercount source any more.
  2. Hi-Z over-cull against the main-depth pyramid on the first frame
     after a scene mutation (no occluders yet rendered). Mitigation
     options: (a) seed pyramid with far-plane sentinel on first frame
     after dirty, (b) switch to Darnell's dedicated low-res occluder
     RT so cull is decoupled from main-pass depth ordering.
  3. BVH fallback (`GpuBvhTree.Build` overflow → non-BVH culling)
     silently dropping commands instead of marking them
     visible-conservative. **See C-GPU-4 below — corrected root-cause
     hypothesis.**
  Each gets a focused log capture: **`XRE_HIZ_CULL_TRACE=1`
  landed (2026-05-12)** in
  [GpuBvhTree.cs](../../../../XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.cs).
  Adds `[GpuBvhTree][trace] ...` line on every overflow with full
  capacity-vs-required figures and a suspect classification
  ("stage-3 malformed tree" vs "real capacity exhaustion"). Default off.
- [x] **C-GPU-4**: Fix `GpuBvhTree` node capacity. The "primitives=52
  nodes=103 overflow" was originally blamed on `EnsureBuffers` being
  undersized for the SAH refine path. **Corrected hypothesis (2026-05-12,
  after C-GPU-3 trace audit):** SAH refine in
  [bvh_sah_refine.comp](../../../../Build/CommonAssets/Shaders/Scene3D/RenderPipeline/bvh_sah_refine.comp)
  rearranges child pointers but does NOT allocate new nodes, so
  `2N-1 = 103` is sufficient capacity for 52 primitives. The math:
  C# `EnsureBuffers` allocates `4 + nodeCount*20 = 2064` node scalars;
  shader's `maxNodesByBuffer = (2064-4)/20 = 103 = totalNodes`, so
  capacity ties exactly (not exceeded). The only remaining trigger for
  `OverflowBvhBit` was **stage-3 malformed-tree detection** in
  [bvh_build.comp](../../../../Build/CommonAssets/Shaders/Scene3D/RenderPipeline/bvh_build.comp)
  — a parent-pointer cycle from a stage-2 race on duplicate Morton
  codes (Karras tie-break in `lcp_leaf` resolves equal codes via
  `clz(i ^ j)` but the resulting overlapping internal-node ranges
  let two distinct internal nodes claim the same child, and stage 2's
  plain `parentIndex` store would then race).

  **Landed (2026-05-12):**
  1. **Capacity headroom** in
     [GpuBvhTree.EnsureBuffers](../../../../XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.cs#L301):
     allocate `nodeCount + 8` slots (~640 extra bytes) so the
     shader's boundary check has unambiguous slack. Frees `OverflowBvhBit`
     to mean exclusively "malformed tree" so the C-GPU-3 trace
     classification is now correct.
  2. **`atomicCompSwap` on `parentIndex`** in
     [bvh_build.comp stage 2](../../../../Build/CommonAssets/Shaders/Scene3D/RenderPipeline/bvh_build.comp):
     only the first writer succeeds (the child's parentIndex was
     initialized to `uint(-1)` at stages 0/1). The losing writer signals
     `OVERFLOW_BVH` explicitly. This deterministically prevents the
     parent-pointer cycle that previously caused stage 3 to detect a
     malformed tree.
  3. The existing suppress-loop (`_bvhBuildSuppressed` in
     [GPUScene.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPUScene.cs#L4338))
     remains as the per-frame retry/log spam guard.

  Build validated clean. Next confirmation: re-run with
  `XRE_HIZ_CULL_TRACE=1` on B1 and confirm the previous 5 overflow
  warnings per session drop to zero (or trace cleanly reports them
  as capacity headroom hits rather than malformed).
- [ ] **C-GPU-5**: Cross-check the `GPURenderOcclusionHiZ.comp` LOD
  selection against the Darnell formula
  `LOD = ceil(log2(sphereWidthNDC * max(viewportWidth, viewportHeight)))`.
  Confirm it samples 4 NDC corners (not center+radius), accounts for
  perspective distortion via the tangent-radius adjustment Darnell
  documents, and uses the maximum of the 4 samples.
  **Audit (2026-05-12, partial)** —
  [GPURenderOcclusionHiZ.comp](../../../../Build/CommonAssets/Shaders/Compute/Occlusion/GPURenderOcclusionHiZ.comp):
  - `HiZOccluded` (primary path, sphere approximation): LOD formula is
    `floor(log2(maxDimPixels))`. Darnell's optimal is
    `ceil(log2(maxDim)) - 1`, which is identical or one LOD finer. Our
    `floor` is **one LOD coarser in some cases** → larger texel
    footprint → larger reported max-depth → under-cull (conservative,
    safe). Not a correctness bug; a small efficiency miss.
  - 4-corner sampling: `SampleHiZConservative` samples the rect's
    four UV corners (`t00`, `t01`, `t10`, `t11`) at the chosen mip
    and takes `max` (or `min` for reversed-Z). Matches Darnell. ✓
  - Perspective distortion: `HiZOccluded` uses a screen-space
    radius approximation (`rNdc = radius / clip.w`); Darnell's
    tangent-radius adjustment is **not present** in the sphere path.
    `HiZOccludedAabbRefine` (called only when sphere result is
    within `|nearestDepth - hiz| < 0.02`) does the proper 8-corner
    AABB→NDC projection, which sidesteps perspective entirely. ✓ for
    the borderline path, ✗ for the primary path.
  - Screen-edge handling: both paths return "visible (uncertain)"
    when `uvMin < 0 || uvMax > 1`. Robust impls clamp the rect to
    [0,1] and test the clipped region. Minor visibility-loss bug on
    large objects near the screen edge.
  - Depth-extent approximation for sphere (`depthDelta = rNdc * 0.5`)
    uses screen-space rather than view-space depth, understating the
    sphere's depth extent. Always conservative (under-culls).
  Decision: LOD formula and corner sampling are spec-compliant or
  strictly more conservative; no correctness bug. **Defer**
  tightening (LOD off-by-one, sphere tangent-radius, edge clipping)
  to a follow-up tier — none would explain a same-frame
  *undercount* of visible meshes, which is what C-GPU-3 is hunting.
- [ ] **C-GPU-6**: Caster shadow culling via Darnell's HZB-shadows
  technique. Build a 2nd HZB from each shadow-casting light's POV; cull
  casters in light space; extrude bbox shadow volumes for non-culled
  casters and re-test against the player's HZB. Deferred until C-GPU-1..5
  stable. Largest correctness uplift on B2 once landed because the
  avatar-heavy scene cost is shadow-pass dominated.
- [ ] **C-GPU-7** (optional, after C-GPU-6): Darnell-style dedicated
  occluder RT (512×256-ish). Artist-tagged occluder geometry only.
  Decouples Hi-Z cull from main pass ordering, removes "first frame
  after mutation" failure mode in C-GPU-3.2.
- [ ] **C-GPU-8**: GPU-resident overdraw debug drawer (parity with main
  pass under GPU occlusion modes). Today
  `VPRC_RenderFullOverdrawPass` re-traverses the CPU render-command list
  via `RenderCommandCollection.RenderCPUFiltered`. Under
  `CpuQueryAsync` we now consult `CpuRenderOcclusionCoordinator.PeekShouldRender`
  (non-mutating peek) so the viz matches the primary pass's visibility
  set. Under `GpuHiZ` + `GpuIndirectZeroReadback`/`GpuIndirectInstrumented`
  the visibility set lives only in GPU buffers (post-cull indirect-draw
  buffer + visibility mask), so the CPU traversal silently shows every
  mesh. Implement a sibling pass that binds the overdraw override
  material + count FBO and reissues `MultiDrawElementsIndirect` against
  the *already-culled* indirect-draw buffer instead of re-traversing on
  the CPU. Same shape as the GPU BVH debug drawer. Until then the pass
  should gate itself off (or warn) when GPU occlusion is active so the
  visualization isn't misleading. Broader audit row: any **secondary
  CPU traversal** (overdraw, wireframe, motion-vector debug, selection
  outline) silently desyncs from GPU culling and needs the same
  treatment — either coordinator-peek + force CPU occlusion, or a
  GPU-resident sibling pass.

Acceptance: ZeroReadback B1 baseline lands within 20% of CpuDirect;
RenderDoc capture shows zero CPU buffer-map operations on the
`GpuIndirectZeroReadback` frame; `XRE_CPU_HIZ_OCCLUSION` flag stays
deleted.

### 10.4 What this means for the current code

| Component | Today | Target |
| --- | --- | --- |
| `PrepareCpuHiZVisibility` / `TryGetCpuHiZVisibility` | gated off by `XRE_CPU_HIZ_OCCLUSION` | DELETED (C-CPU-2) |
| Temporal occlusion hysteresis dict in `GPURenderPassCollection.Occlusion` | dormant | DELETED (C-CPU-2) |
| `_cpuHiZVisibleMeshCommands` / `_cpuHiZTemporalOcclusion` / `_cpuHiZPendingFence` | unused with flag off | DELETED (C-CPU-2) |
| `VisualScene3D.ShouldMaintainCpuGpuCommandMirror()` CpuDirect+GpuHiZ branch | gated by `XRE_CPU_HIZ_OCCLUSION` | DELETED, Surfel-GI branch keeps (C-CPU-2) |
| `Engine.Rendering.Settings.GpuOcclusionCullingMode` getter | env override only | env override + path-coercion (C-CPU-1) |
| `CpuRenderOcclusionCoordinator` | optional `CpuQueryAsync` mode | DEFAULT for CpuDirect (C-CPU-1 makes this automatic) |
| `GpuBvhTree.EnsureBuffers` overflow path | logs + non-BVH fallback | grow capacity, never silently drop commands (C-GPU-4) |
| `_hiZDepthPyramid` source | main depth attachment | unchanged for now; consider dedicated occluder RT (C-GPU-7) |
| `_culledSceneToRenderBuffer` consumers on CPU | several (snapshot, GPU debug, hybrid validator) | only `IndirectDebug.*` opt-in diagnostics (C-GPU-1) |
| `GPURenderOcclusionHiZ.comp` LOD formula | unverified | matches Darnell formula (C-GPU-5) |
| Caster shadow HZB | not implemented | second HZB + shadow-volume re-test (C-GPU-6) |

### 10.5 Execution order

1. **C-GPU-1** (audit) — read-only, cheap, immediately informs everything else.
2. **C-CPU-1 + C-CPU-2** — small surgical changes, restores CpuDirect to a
   clean state and lets us delete a lot of code.
3. **C-GPU-3** — find and fix the underlying undercount. Capture artifacts
   land in the Findings Log.
4. **C-GPU-4** — size BVH correctly.
5. **C-GPU-5** — verify LOD math matches Darnell.
6. Re-measure all three baselines. Update §1 numbers.
7. **C-GPU-6** (shadow HZB) — largest B2 win once the basic path is solid.
8. **C-GPU-7** (dedicated occluder RT) only if §1 still misses target.
9. **C-CPU-3** (software occluder) only if hardware queries don't cull
   enough on B2 (avatar-heavy).
10. **C-GPU-8** (GPU-resident overdraw debug drawer) — fold in once
    GPU indirect-draw buffers are the canonical visibility set
    (post C-GPU-3..6). Lightweight gating today; full sibling pass later.

## 11. Related Code

- [HybridRenderingManager.cs](../../../../XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs)
- [GPUScene.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPUScene.cs)
- [RenderCommandCollection.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/RenderCommandCollection.cs)
- [GPURenderPassCollection.Occlusion.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection.Occlusion.cs)
- [CpuRenderOcclusionCoordinator.cs](../../../../XREngine.Runtime.Rendering/Rendering/Occlusion/CpuRenderOcclusionCoordinator.cs)
- [VisualScene3D.cs](../../../../XREngine.Runtime.Rendering/Rendering/VisualScene3D.cs)
- [GpuBvhTree.cs](../../../../XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.cs)
- [VPRC_BuildAccelerationStructure.cs](../../../../XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_BuildAccelerationStructure.cs)
- [Engine.Rendering.Settings.cs](../../../../XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs)
- [docs/architecture/rendering/mesh-submission-strategies.md](../../../architecture/rendering/mesh-submission-strategies.md)
