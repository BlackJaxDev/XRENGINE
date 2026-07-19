# GPU-Driven Occlusion Culling Architecture TODO

Last Updated: 2026-07-01
Owner: Rendering
Status: Not started (design/todo)
Target Branch: `rendering-gpu-driven-occlusion`

Research sources:

- [GPU-Driven Rendering Pipelines (Haar & Aaltonen, SIGGRAPH 2015)](https://advances.realtimerendering.com/s2015/aaltonenhaar_siggraph2015_combined_final_footer_220dpi.pdf)
- [Two-Pass Occlusion Culling](https://medium.com/@mil_kru/two-pass-occlusion-culling-4100edcad501)
- [A Deep Dive into Nanite Virtualized Geometry (SIGGRAPH 2021)](https://advances.realtimerendering.com/s2021/Karis_Nanite_SIGGRAPH_Advances_2021_final.pdf)
- [vkguide: GPU Driven Rendering & Compute Culling](https://vkguide.dev/docs/gpudriven/compute_culling/)
- [Patch-Based Occlusion Culling / HZB generation notes (GPU Gems 2 ch. 6 background)](https://developer.nvidia.com/gpugems/gpugems2/part-i-geometric-complexity/chapter-6-hardware-occlusion-queries-made-useful)

Related local docs:

- [Frame Lifecycle And Dispatch Paths](../../../../architecture/rendering/frame-lifecycle-and-dispatch-paths.md)
- [Mesh Submission Strategies](../../../../architecture/rendering/mesh-submission-strategies.md)
- [Default Render Pipeline Notes](../../../../architecture/rendering/default-render-pipeline-notes.md)
- [GPU Meshlet Zero-Readback Rendering Design](../../design/rendering/gpu-meshlet-zero-readback-rendering-design.md)
- [Render Submission Perf Debug Plan](../../design/rendering/render-submission-perf-debug-plan.md)
- [CPU Async Hardware Query Occlusion TODO](../optimization/cpu-async-hardware-query-occlusion-todo.md)
- [Masked Software Occlusion Culling TODO](../masked-software-occlusion-culling-todo.md)
- [Production Rendering Pipeline Roadmap](production-rendering-pipeline-roadmap.md)

## Goal

Define and implement a production-quality, fully zero-readback GPU-driven
occlusion culling architecture for the Vulkan backend (OpenGL keeps parity
where the shared GLSL allows), improving on the current "GPU BVH" option so
that BVH traversal, Hi-Z occlusion, and indirect draw generation form one
coherent GPU pipeline with persistent GPU-resident visibility — no CPU count
readback, no full-passthrough "dirty" frames, and no CPU-side occlusion
verdicts on the GPU dispatch path.

Non-goals:

- Replacing the CPU-direct occlusion paths (`CpuQueryAsync`, CPU SOC). Those
  remain the CpuDirect story; this doc only bounds their scope.
- Per-triangle visibility (virtual geometry). Meshlet-level integration is in
  scope; triangle-level is not.

## Current Baseline (what exists today)

Dispatch-path culling order (`GPURenderPassCollection`):

```text
frustum OR BVH cull compute -> (optional) GpuHiZ occlusion refine -> indirect build -> MultiDraw*IndirectCount
```

- Frustum culling: `GPURenderCulling.comp` / `GPURenderCullingSoA.comp` write
  compact commands to `_culledSceneToRenderBuffer` + counts to
  `_culledCountBuffer`.
- GPU BVH (`GpuBvhTree`, `bvh_frustum_cull.comp`): internal command-bounds BVH
  with GPU build/refit and stable LBVH construction. Traversal is **frustum-only** — interior
  nodes are never tested against any occlusion source. GPU submission strategies
  select it automatically when supported; the flat GPU frustum path remains the
  readiness fallback when the BVH shader or provider is unavailable.

Current substrate (2026-07-18): the scene BVH now uses compact 48-byte nodes,
stable radix construction for large inputs, revision-aware build/refit, and a
bounded plane-masked root-down traversal. Node-level Hi-Z should extend that
queue and node format rather than introducing a parallel traversal contract;
see [GPU Scene BVH](../../../../architecture/rendering/gpu-scene-bvh.md).
- GpuHiZ (`GPURenderOcclusionHiZ.comp` + `ApplyGpuHiZOcclusion`): single-phase
  refine against a depth pyramid built from current or history depth.
  Weaknesses:
  - **Dirty bypass**: any scene mutation or camera jump skips refine for the
    frame and passes every frustum/BVH survivor through
    (`ShouldInvalidateGpuHiZTemporalState` + `XRE_GPU_HIZ_DIRTY_BYPASS`,
    default ON). Sustained editing or motion collapses to zero occlusion.
  - **Self-occlusion hazards**: current-depth pyramids already contain the
    candidates being tested; the forward depth-normal prepass forces a full
    refine bypass on `OpaqueForward`/`MaskedForward`
    (`ShouldBypassCurrentDepthGpuHiZRefine`).
  - Zero-readback refine dispatch sizes by full buffer element count because
    the CPU count mirror is stale (Phase-7 audit fix) — correct but wasteful.
  - Pyramid rebuild is per-pass unless `CacheGpuHiZOcclusionOncePerFrame`.
- Occlusion mode routing: on Vulkan non-Diagnostics profiles `CpuQueryAsync`
  is coerced to `GpuHiZ`; the GPU-dispatch `CpuQueryAsync` submit path is
  OpenGL-only; GPU-dispatch CPU SOC requires instrumented count readback and
  intentionally no-ops under zero-readback.
- Visibility state is **not persistent on the GPU**: each pass re-culls from
  scratch; temporal hysteresis lives in CPU-side dictionaries
  (`_temporalOcclusion`) that require count readback to function.

## Design Principles

- Zero readback is the contract, not an optimization: production Vulkan modes
  (`GpuIndirectZeroReadback`, `GpuMeshletZeroReadback`) must never read counts,
  visibility, or overflow flags on the CPU in the frame loop. Diagnostics go
  through GPU-written stats buffers polled at low frequency by the profiler
  readback path.
- Previous-frame visibility is data, not heuristics: a persistent GPU
  visibility buffer replaces CPU temporal dictionaries and dirty-frame
  passthrough.
- Occlusion belongs inside the hierarchy: BVH interior nodes should be
  rejected against the depth pyramid during traversal, not only per-command
  after traversal.
- Disocclusion must be recovered same-frame (two-phase), not next-frame
  (single-phase + hysteresis) — no visible popping on camera cuts.
- Conservative correctness: any uncertain state (new command, resized view,
  missing pyramid) resolves to visible. False occlusion is a bug; false
  visibility is a cost.
- Every fallback is observable: telemetry distinguishes phase-1/phase-2 draw
  counts, node rejections, and any conservative-visible passthrough with a
  reason.

## Target Architecture

Two-phase Hi-Z occlusion with persistent GPU visibility, BVH-integrated:

```text
Phase 1 (main visibility):
  frustum/BVH cull (BVH nodes tested vs LAST frame's pyramid)
    -> emit commands whose visibility bit was set last frame
    -> indirect draw phase-1 set (writes depth + color)
  build Hi-Z pyramid from phase-1 depth

Phase 2 (disocclusion recovery):
  re-test phase-1-rejected candidates vs CURRENT pyramid
    -> emit newly visible commands, update visibility bits
    -> indirect draw phase-2 set (depth + color)
  visibility buffer now reflects this frame; no CPU involvement
```

Key structures:

- **Visibility buffer**: one persistent SSBO per (scene, view) keyed by stable
  source-command index: 1 visibility bit + small age/confidence counter packed
  per command. Survives frames; compaction/mutation hooks update it when
  `GPUScene` adds/removes commands (default new = visible).
- **Per-view state**: `GPUViewSet` views get independent visibility words (or
  a per-view bitplane) so stereo eyes never share a mono verdict incorrectly;
  shared-stereo draws OR the two eyes' bits.
- **BVH node visibility**: optional per-node last-visible frame id so fully
  occluded subtrees are skipped in phase 1 and only re-tested in phase 2.

## Phase 0 - Branch And Baseline

- [ ] Create branch `rendering-gpu-driven-occlusion`.
- [ ] Capture baseline profiles in the Unit Testing World (Vulkan +
  `GPURenderDispatch=true`, DevParity and ShippingFast profiles): FPS, cull
  dispatch ms, HiZ stage stats (`HiZStageStats`), draw counts with `GpuHiZ`
  on/off, plus BVH-ready and flat-fallback frame counts.
- [ ] Capture one RenderDoc frame per configuration under
  `Build/_AgentValidation/<run>/renderdoc/` documenting current pass order.
- [ ] Record the count of dirty-bypass frames during 30s of editor camera
  motion (this is the number two-phase should drive to ~0).

## Phase 1 - Contracts And Audit

- [ ] Document the buffer contract for cull -> occlusion -> indirect build in
  `docs/architecture/rendering/` (who writes `_culledSceneToRenderBuffer`,
  `_culledCountBuffer`, swap semantics of `SwapCulledBufferAfterOcclusion`,
  barrier obligations per backend).
- [ ] Define the visibility-buffer format (bit layout, per-view addressing,
  stable-index remap rules on GPUScene compaction) and add it to the contract
  doc.
- [ ] Audit `GPUScene` stable command indices across add/remove/compaction to
  guarantee visibility bits can be remapped GPU-side (compute copy on
  compaction) without readback.
- [ ] Decide pyramid ownership: one shared pyramid per (pipeline, view) per
  frame (extend `CacheGpuHiZOcclusionOncePerFrame` to default ON) vs.
  per-pass. Two-phase requires the shared model; justify any exception.
- [ ] Vulkan sync audit: enumerate barriers between cull writes, pyramid
  build (compute), phase-2 dispatch, and indirect count draws under
  synchronization2; confirm OpenGL equivalents map to existing
  `MemoryBarrier` masks.

Acceptance criteria:

- [ ] A written contract exists that a reviewer can validate a RenderDoc
  capture against, for both backends.

## Phase 2 - Persistent GPU Visibility State

- [ ] Add per-(scene, view) visibility SSBO managed by
  `GPURenderPassCollection`/`GPUScene`, sized to source command capacity,
  default-visible on allocation and on command add.
- [ ] GPU-side remap/compaction kernel invoked from existing GPUScene
  mutation hooks (no CPU mirror).
- [ ] Replace the CPU `_temporalOcclusion` dictionary and
  `ShouldInvalidateGpuHiZTemporalState` heuristics on the zero-readback path
  with visibility-buffer age/confidence updates written by the occlusion
  shaders.
- [ ] Keep the CPU temporal filter only for `GpuIndirectInstrumented`
  diagnostics (it depends on readback by definition).
- [ ] Telemetry: GPU stats slots for visible/occluded/aged counts written by
  compute, surfaced through the existing async stats readback (not the frame
  loop).

Acceptance criteria:

- [ ] Scene mutation (add/remove model in the editor) does not reset all
  visibility to visible and does not require a passthrough frame.
- [ ] No new per-frame CPU readbacks appear in
  `Stats.GpuReadback` under zero-readback modes.

## Phase 3 - Two-Phase Hi-Z (replaces dirty bypass)

- [ ] Split the per-pass GPU flow into phase-1 emit (visibility bit test in
  the cull shader — no Hi-Z sampling needed) and phase-2 re-test dispatch
  (`GPURenderOcclusionHiZ.comp` derivative that tests only phase-1 rejects
  against the freshly built pyramid, appends newly visible, and updates bits).
- [ ] Build the pyramid once per view from phase-1 depth; drop the
  history-vs-current depth selection and the forward-prepass self-cull bypass
  (phase-1 depth cannot self-occlude phase-2 candidates that weren't drawn).
- [ ] Emit two indirect batches per pass (phase-1, phase-2) or one combined
  append buffer with a second count slot; both drawn via
  `MultiDraw*IndirectCount` with no CPU count knowledge.
- [ ] Delete/param-gate `XRE_GPU_HIZ_DIRTY_BYPASS`: camera jumps and scene
  mutations are handled naturally (phase 1 draws last-visible, phase 2
  recovers the rest). Keep the explicit memory-barrier contract from the
  bypass branch (see `gpu-hiz-dirty-bypass-phase4` notes) on both dispatch
  points.
- [ ] Preserve pass-awareness rules: shadow passes and depth-variant passes
  remain occlusion-exempt.
- [ ] OpenGL: same shader flow behind GL 4.6 compute; instrumented mode may
  additionally read counts for diagnostics.

Acceptance criteria:

- [ ] Sustained editor camera motion produces zero passthrough frames and no
  visible popping (validated by MCP screenshot iteration + telemetry).
- [ ] Camera cut recovers full visibility in exactly one frame (phase 2),
  not `TemporalOcclusionHysteresisFrames`.
- [ ] Total draws for a static camera ≤ current single-phase GpuHiZ draws.

## Phase 4 - BVH-Integrated Occlusion Traversal

- [ ] Extend `bvh_frustum_cull.comp` traversal to optionally test interior
  node AABBs against the previous-frame pyramid (phase 1) so occluded
  subtrees are rejected without visiting leaves; occluded nodes push their
  ranges to a phase-2 node list instead of descending.
- [ ] Phase-2 node recovery: re-test the deferred node list against the
  current pyramid, then run leaf-level tests for surviving nodes.
- [ ] Add per-node last-visible frame id (small SSBO parallel to the node
  buffer) with the same GPU-side remap rules as command visibility.
- [ ] Validate BVH traversal parity vs. flat frustum cull (same visible set
  ± phase-2 timing) with a deterministic test scene.
- [x] Make GPU BVH selection strategy-driven for Vulkan instrumented,
  zero-readback, and meshlet submission; remove the Vulkan environment gate.
- [ ] Keep refit/rebuild policy as-is (dirty-marking via GPUScene hooks);
  document that a stale-refit BVH only costs conservatism, not correctness,
  because node tests use enlarged parent bounds.

Acceptance criteria:

- [ ] In a high-occlusion scene, BVH+occlusion traversal visits measurably
  fewer nodes/commands than flat cull + per-command Hi-Z (GPU stats slots).
- [x] GPU BVH is on by default for Vulkan zero-readback and no longer
  labeled diagnostic.

## Phase 5 - Meshlet Path Integration

- [ ] Feed the same per-view pyramid and visibility model into
  `MeshletCulling.task` / `MeshletCullingExt.task` (they already consume
  `TryGetHiZDepthPyramidForMeshlets`): meshlet-level phase-2 recovery via a
  second task dispatch or task-shader append path.
- [ ] Ensure command-level phase 1 never rejects a command whose meshlets
  could survive (defer to meshlet tests when the meshlet path owns the
  command), matching the existing forward-prepass parity rule.
- [ ] Meshlet stats: phase-1/phase-2 meshlet counts in the existing stats
  buffer.

## Phase 6 - Stereo / VR Correctness

- [ ] Per-view visibility words in the visibility buffer addressed by
  `GPUViewDescriptor.ViewId`; shared-stereo draws OR left/right bits before
  emit.
- [ ] One pyramid per eye (or a layered pyramid) — never test the right eye
  against the left eye's depth.
- [ ] Multiview (`VK_KHR_multiview`) path: confirm phase-1/phase-2 batches
  work per-view with `GPUViewSet` offsets; no mono collapse.
- [ ] Scene-only emulated VR smoke + OpenXR/Monado smoke with per-eye
  draw-count telemetry.

## Phase 7 - Mode Consolidation And Settings

- [ ] `EOcclusionCullingMode.GpuHiZ` becomes the two-phase implementation on
  GPU dispatch paths (no new enum value; behavior upgrade). Document it.
- [ ] Remove the GPU-dispatch `CpuQueryAsync` scaffold (OpenGL-only,
  readback-dependent) or hard-gate it to `GpuIndirectInstrumented` +
  Diagnostics; the coerce-to-GpuHiZ warning on Vulkan stays.
- [ ] CPU SOC stays CpuDirect-only. The zero-readback SOC no-op warning
  (`CpuSocGpuReadbackDisabled`) is already explicit — keep it.
- [ ] CpuDirect is unaffected: `CpuQueryAsync` (now valid on Vulkan via
  hostQueryReset) and CPU SOC remain the CPU-path options; `GpuHiZ` on
  CpuDirect remains an explicit no-op (documented).
- [ ] Settings/ImGui: Occlusion panel gains phase-1/phase-2 rows, node
  rejection counts, and visibility-buffer occupancy; deprecate the dirty
  bypass toggle from `EditorPreferences` once removed.
- [ ] Update `docs/architecture/rendering/mesh-submission-strategies.md`,
  `default-render-pipeline-notes.md`, and the occlusion feature docs.

## Phase 8 - Validation

- [ ] Unit tests (`XREngine.UnitTests/Rendering`): visibility-buffer remap on
  compaction, phase ordering source contracts, mode-routing matrix
  (backend × profile × strategy × mode), stereo OR-combine semantics.
- [ ] Editor smoke (Vulkan, GPU dispatch ON): still camera, slow orbit, fast
  flight, camera cut, live model import — MCP screenshots from ≥2 camera
  positions each; zero false-occlusion artifacts.
- [ ] RenderDoc: capture one frame per phase milestone; export pyramid mips
  and phase-2 append buffers; verify barrier placement matches the Phase-1
  contract doc.
- [ ] Perf gates vs. Phase-0 baseline in the high-occlusion scene:
  - [ ] GPU cull+occlusion time ≤ baseline GpuHiZ refine time + 15%.
  - [ ] Total draws reduced in occluded viewpoints (target ≥ 30% fewer than
    frustum-only).
  - [ ] Zero CPU readback bytes per frame in zero-readback modes
    (`Stats.GpuReadback`).
- [ ] OpenGL parity run (instrumented + zero-readback) on the same scene.

## Open Questions

- Phase-2 granularity for the BVH path: re-test deferred nodes only, or
  re-test all phase-1 rejects flat? (Node-only is cheaper; flat is simpler
  and bounds worst-case latency at one frame regardless.)
- Should the visibility buffer live per render pass or per view with a pass
  mask? Per-view is smaller; per-pass avoids cross-pass aliasing when pass
  masks differ.
- Async compute: pyramid build and phase-2 cull are natural async-queue work
  on Vulkan (`QueueOverlap=GraphicsComputeTransfer` already exists) — worth
  scheduling there in Phase 3, or defer to a later perf pass?
- Do we need a conservative screen-size floor (skip occlusion tests for
  near/huge AABBs) to avoid phase-2 churn on architectural shells, or does
  node-level rejection make that moot?
- OpenGL: is two-phase worth full parity, or should OpenGL keep single-phase
  GpuHiZ + instrumented diagnostics only?

## Final Task

- [ ] Merge `rendering-gpu-driven-occlusion` back into `master` after all
  phases validate.
